using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.ViewModels;

public sealed class InstallerWizardViewModel : ObservableObject
{
    private static readonly TimeSpan DependencyInstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Matches the activation timeout the installation itself runs under.</summary>
    private static readonly TimeSpan RollbackActivationTimeout = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<(InstallerPage Page, string Title)> Steps =
    [
        (InstallerPage.Diagnose, "System check"),
        (InstallerPage.Dependencies, "Prerequisites"),
        (InstallerPage.Package, "LocalAi package"),
        (InstallerPage.Models, "Models"),
        (InstallerPage.Residency, "Video memory"),
        (InstallerPage.Agents, "Client apps"),
        (InstallerPage.Confirm, "Confirm"),
        (InstallerPage.Progress, "Install"),
        (InstallerPage.Finish, "Finished"),
    ];

    private readonly DiagnosePageViewModel diagnose = new();
    private readonly DependenciesPageViewModel dependencies = new();
    private readonly IInstalledApplicationProbe installedApplications;
    private readonly PackagePageViewModel package = new();

    /// <summary>
    /// Kept for the whole run so the executable approved once is the one revalidated before each
    /// later install, rather than a fresh answer from a search path that may have changed.
    /// </summary>
    private readonly TrustedWingetSource wingetSource =
        new(new WindowsWingetExecutableTrust());
    private readonly ModelsPageViewModel models = new();
    private readonly ResidencyPageViewModel residency = new();
    private readonly AgentIntegrationPageViewModel agents = new();
    private readonly ReviewApplyPageViewModel review = new();
    private readonly FinishPageViewModel finish = new();
    private readonly WindowsEnvironmentDetector environmentDetector;
    private readonly IProcessRunner processRunner;

    private readonly AnonymousReleaseFeed anonymousFeed = new();
    private CancellationTokenSource? runCancellation;
    private InstallerPage currentPage = InstallerPage.Diagnose;
    private bool isRunning;
    private bool isComplete;
    private bool hasRunError;
    private bool wasCancelled;
    private bool hasInitialized;
    private int progress;
    private string progressText = "Ready";
    private string? runLogMessage;
    private EnvironmentDiagnosis? environmentDiagnosis;
    private CatalogRecommendation lastRecommendation = CatalogRecommendation.Empty;
    private string? resolvedTag;
    private string? packageOutcome;
    private bool packageInstalled;
    private InstallerRunJournal? runJournal;
    private InstallerRunJournal? interruptedJournal;
    private string? interruptedRunNotice;

    public InstallerWizardViewModel()
    {
        var runner = new SystemProcessRunner();
        processRunner = runner;
        // Kept, not just handed to the detector: the detector's answer may have come from a
        // plain PATH lookup, and only the validated one is worth recording as launchable.
        installedApplications = new WindowsInstalledApplicationProbe();
        environmentDetector = new WindowsEnvironmentDetector(
            new SystemEnvironmentProbe(),
            new SystemFileSystemProbe(),
            runner,
            installedApplications,
            new SystemDiskProbe(),
            new SystemNetworkProbe(),
            new WindowsGpuProbe(new DxgiNativeGpuAdapterEnumerator()));

        BackCommand = new RelayCommand(() => MovePrevious(), () => CanMovePrevious);
        NextCommand = new RelayCommand(() => MoveNext(), () => CanMoveNext);
        InstallCommand = new AsyncRelayCommand(() => RunAsync(), () => CanRun);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        RollbackCommand = new AsyncRelayCommand(() => RollbackThisRunAsync(), () => CanRollback);
        RollbackPreviousRunCommand = new AsyncRelayCommand(
            () => RollbackPreviousRunAsync(),
            () => HasInterruptedRun && !isRunning);

        // Relaxing the residency policy immediately widens what the models page can offer,
        // so the two pages stay consistent instead of contradicting each other.
        residency.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ResidencyPageViewModel.Policy))
            {
                OnResidencyChanged();
            }
        };

        // The folder the installer was started from, when someone has already put a release
        // beside it. AppContext.BaseDirectory rather than the current directory: a wizard is
        // usually launched from Explorer, whose current directory is not where the file is.
        package.OfferLocalFolder(AppContext.BaseDirectory);
        RebuildSteps();
    }

    public event EventHandler? CloseRequested;

    public bool EnableDependencyActions { get; set; }

    /// <summary>
    /// Where the run report and the run journal are written. Outside the LocalAi root on
    /// purpose: that tree is validated against an exact name list on every install, so a
    /// stray file inside it would refuse the next installation. Settable so tests can point
    /// the journal at their own directory instead of the machine's.
    /// </summary>
    public string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi-installer-logs");

    public RelayCommand BackCommand { get; }

    public RelayCommand NextCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand RollbackCommand { get; }

    public AsyncRelayCommand RollbackPreviousRunCommand { get; }

    public DiagnosePageViewModel Diagnose => diagnose;
    public DependenciesPageViewModel Dependencies => dependencies;
    public PackagePageViewModel Package => package;
    public ModelsPageViewModel Models => models;
    public ResidencyPageViewModel Residency => residency;
    public AgentIntegrationPageViewModel Agents => agents;
    public ReviewApplyPageViewModel Review => review;
    public FinishPageViewModel Finish => finish;

    public ObservableCollection<WizardStep> StepList { get; } = [];

    public InstallerPage CurrentPage
    {
        get => currentPage;
        private set
        {
            if (currentPage == value)
            {
                return;
            }

            currentPage = value;
            RefreshAll();
        }
    }

    public string StepTitle => CurrentPage switch
    {
        InstallerPage.Diagnose => "Checking this computer",
        InstallerPage.Dependencies => "Prerequisites",
        InstallerPage.Package => "LocalAi package",
        InstallerPage.Models => "Local models",
        InstallerPage.Residency => "Video memory requirements",
        InstallerPage.Agents => "Client applications",
        InstallerPage.Confirm => "Ready to install",
        InstallerPage.Progress => "Installing",
        _ => hasRunError ? "Installation not completed" : "Installation complete",
    };

    public string StepDescription => CurrentPage switch
    {
        InstallerPage.Diagnose =>
            "Results of the environment check. Items marked as a warning still allow " +
            "installation.",
        InstallerPage.Dependencies =>
            "Choose which prerequisites to install. Nothing is selected for you.",
        InstallerPage.Package => "Choose the LocalAi release to install.",
        InstallerPage.Models => "Choose which local models to set up.",
        InstallerPage.Residency =>
            "Decide how strictly models must fit into video memory.",
        InstallerPage.Agents =>
            "Choose how each client application should be integrated.",
        InstallerPage.Confirm =>
            "Review what is about to happen. To change anything click Back; to apply it " +
            "click Install.",
        InstallerPage.Progress => "Applying the selected actions.",
        _ => hasRunError
            ? "Some actions did not complete. The log below shows what happened."
            : "All selected actions completed.",
    };

    public string StepStatus => $"Step {StepIndex(CurrentPage) + 1} of {Steps.Count}";

    public bool IsFinishPage => CurrentPage == InstallerPage.Finish;

    public bool IsProgressPage => CurrentPage == InstallerPage.Progress;

    public bool IsRunning => isRunning;

    public bool IsComplete => isComplete;

    public bool HasRunError => hasRunError;

    public bool IsCanceled => wasCancelled;

    public int Progress => progress;

    public string ProgressText => progressText;

    public string? RunLog => runLogMessage;

    public string? FinishSummary => finish.Summary;

    /// <summary>
    /// Whether the last run left reversible effects a rollback could take back. False for a
    /// successful run on purpose: rollback is a recovery from a run that went wrong, never
    /// an uninstaller for one that went right.
    /// </summary>
    public bool CanRollback =>
        IsFinishPage &&
        !isRunning &&
        runJournal is { } journal &&
        journal.Snapshot.Outcome is InstallerRunOutcome.Failed or InstallerRunOutcome.Cancelled &&
        journal.Snapshot.HasReversibleWork;

    public bool HasInterruptedRun => interruptedJournal is not null;

    public string? InterruptedRunNotice => interruptedRunNotice;

    public bool HasInterruptedRunNotice => !string.IsNullOrWhiteSpace(interruptedRunNotice);

    public int CurrentPageIndex => StepIndex(CurrentPage);

    /// <summary>
    /// Back and Next stay on screen for the whole wizard and only change availability.
    /// Buttons that vanish make the panel jump and hide where the user is.
    /// </summary>
    public bool CanMovePrevious =>
        CurrentPage is not (InstallerPage.Diagnose or InstallerPage.Progress) &&
        !isRunning &&
        !IsFinishPage;

    public bool CanMoveNext => !isRunning && CurrentPage switch
    {
        InstallerPage.Diagnose => diagnose.CanContinue,
        InstallerPage.Dependencies => dependencies.CanContinue,
        InstallerPage.Package => package.CanContinue,
        InstallerPage.Models => models.CanContinue,
        InstallerPage.Residency => residency.CanContinue,
        InstallerPage.Agents => agents.CanContinue,
        _ => false,
    };

    public bool CanRun =>
        CurrentPage == InstallerPage.Confirm && !isRunning && review.CanApply;

    public bool CanCancel => !IsFinishPage || !isRunning;

    public bool IsNextVisible => CurrentPage is not (
        InstallerPage.Confirm or InstallerPage.Progress or InstallerPage.Finish);

    public bool IsInstallVisible => CurrentPage == InstallerPage.Confirm;

    public string CancelButtonText => IsFinishPage ? "Close" : "Cancel";

    public string? ReviewText => BuildReview();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (hasInitialized)
        {
            return;
        }

        LoadInterruptedRunJournal();
        await RefreshEnvironmentDiagnosticsAsync(cancellationToken);
        hasInitialized = true;
    }

    /// <summary>
    /// Looks for a journal from a run that never wrote its outcome — a wizard that was
    /// killed, or a machine that lost power, mid-installation. Its effects are real and
    /// undocumented anywhere else, so the first page says what they were and offers to
    /// undo the reversible ones before the user commits to a fresh run on top of them.
    /// </summary>
    public void LoadInterruptedRunJournal()
    {
        try
        {
            interruptedJournal = InstallerRunJournal.FindInterrupted(LogDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            interruptedJournal = null;
        }

        interruptedRunNotice = interruptedJournal is { } journal
            ? BuildInterruptedRunNotice(journal.Snapshot)
            : null;
        RefreshAll();
    }

    private static string BuildInterruptedRunNotice(InstallerRunJournalSnapshot snapshot)
    {
        var notice = new StringBuilder();
        notice.AppendLine(
            $"A previous installer run ({snapshot.StartedAtUtc:yyyy-MM-dd HH:mm} UTC) was " +
            "interrupted before it could finish. What it recorded:");
        foreach (var step in snapshot.Steps)
        {
            var state = step.Status switch
            {
                InstallerRunStepStatus.Completed => "applied",
                InstallerRunStepStatus.Running => "started, state unknown",
                InstallerRunStepStatus.Failed => "failed",
                _ => step.Status.ToString(),
            };
            notice.AppendLine($"  - {step.Description}: {state}.");
        }

        if (snapshot.Steps.Count == 0)
        {
            notice.AppendLine("  - Nothing was applied before it stopped.");
        }

        notice.Append(snapshot.HasReversibleWork
            ? "You can roll back the reversible changes now, or continue and leave them " +
                "in place."
            : "Nothing it recorded is reversible by this installer. Continue to leave it " +
                "as it is.");
        return notice.ToString();
    }

    public void SetReviewConfirmed(bool confirmed)
    {
        review.IsConfirmed = confirmed;
        RefreshAll();
    }

    public bool MoveNext()
    {
        if (!CanMoveNext)
        {
            return false;
        }

        // Moving past the first page with the interrupted-run notice on screen is the
        // explicit "leave it in place" choice the notice offers. Recording it stops every
        // later wizard start from asking about the same abandoned run again.
        if (CurrentPage == InstallerPage.Diagnose && interruptedJournal is { } abandoned)
        {
            try
            {
                abandoned.Finish(InstallerRunOutcome.Abandoned);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // An unwritable journal must not trap the user on the first page.
            }

            interruptedJournal = null;
            interruptedRunNotice = null;
        }

        CurrentPage = CurrentPage switch
        {
            InstallerPage.Diagnose => InstallerPage.Dependencies,
            InstallerPage.Dependencies => InstallerPage.Package,
            InstallerPage.Package => InstallerPage.Models,
            InstallerPage.Models => InstallerPage.Residency,
            InstallerPage.Residency => InstallerPage.Agents,
            InstallerPage.Agents => InstallerPage.Confirm,
            _ => CurrentPage,
        };
        return true;
    }

    public bool MovePrevious()
    {
        if (!CanMovePrevious)
        {
            return false;
        }

        CurrentPage = CurrentPage switch
        {
            InstallerPage.Dependencies => InstallerPage.Diagnose,
            InstallerPage.Package => InstallerPage.Dependencies,
            InstallerPage.Models => InstallerPage.Package,
            InstallerPage.Residency => InstallerPage.Models,
            InstallerPage.Agents => InstallerPage.Residency,
            InstallerPage.Confirm => InstallerPage.Agents,
            _ => CurrentPage,
        };
        return true;
    }

    /// <summary>
    /// Cancels a running installation, or closes the wizard when nothing is running. Cancel
    /// is never disabled while work is in flight: it is the only way out of a long install.
    /// </summary>
    public void Cancel()
    {
        if (isRunning)
        {
            runCancellation?.Cancel();
            return;
        }

        wasCancelled = !IsFinishPage;
        OnPropertyChanged(nameof(IsCanceled));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRun)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCancellation = linked;
        var token = linked.Token;

        isRunning = true;
        isComplete = false;
        hasRunError = false;
        CurrentPage = InstallerPage.Progress;
        RefreshAll();

        var report = new StringBuilder();
        var successfulActions = 0;
        var failedActions = 0;
        var skippedActions = 0;
        var requested = 0;

        try
        {
            if (!EnableDependencyActions)
            {
                finish.Progress = report.ToString().Trim();
                finish.Summary = "Dry run completed. Nothing was installed.";
                SetProgress(100, "Completed");
                isComplete = true;
                CurrentPage = InstallerPage.Finish;
                return true;
            }

            // Before the first effect, so a process killed at any later point still leaves
            // a record. A journal that cannot be written costs the run its rollback, never
            // the installation itself - but it has to say so, because a user who believes
            // a rollback exists is worse off than one who knows it does not.
            try
            {
                runJournal = InstallerRunJournal.Start(LogDirectory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    NotSupportedException or ArgumentException)
            {
                runJournal = null;
                AppendLog(
                    report,
                    $"Run journal could not be created ({exception.Message}). The run " +
                    "continues, but rollback will not be available for it.");
            }

            await RefreshEnvironmentDiagnosticsAsync(token);

            var selected = dependencies.Dependencies
                .Where(dependency => dependency.IsConsented && dependency.IsInstallable)
                .ToArray();
            requested = selected.Length;
            AppendLog(report, selected.Length == 0
                ? "No prerequisites were selected."
                : $"Prerequisites selected: {selected.Length}.");

            var step = 0;
            foreach (var dependency in selected)
            {
                token.ThrowIfCancellationRequested();
                SetProgress(
                    10 + (80 * step++ / Math.Max(selected.Length, 1)),
                    $"Installing {dependency.Title}...");

                var definition = ResolveDependencyDefinition(dependency.Id);
                if (definition is null)
                {
                    AppendLog(report, $"{dependency.Title}: no automated installer available.");
                    skippedActions++;
                    continue;
                }

                AppendLog(report, $"{dependency.Title}: installing...");
                var dependencyStep = JournalBegin(
                    InstallerRunEffectKind.DependencyInstall,
                    $"Prerequisite {dependency.Title} ({definition.PackageId})");
                var installed = definition.InstallerKind switch
                {
                    DependencyInstallerKind.WinGet =>
                        await TryInstallDependencyWithWingetAsync(
                            definition,
                            dependency.Title,
                            reinstall: dependency.IsInstalled,
                            token),
                    DependencyInstallerKind.Npm =>
                        await TryInstallDependencyWithNpmAsync(
                            definition,
                            dependency.Title,
                            token),
                    _ => false,
                };
                AppendLog(report, installed
                    ? $"{dependency.Title}: done."
                    : $"{dependency.Title}: failed.");
                if (installed)
                {
                    successfulActions++;
                    JournalComplete(
                        dependencyStep,
                        "Installed machine-wide. This installer does not uninstall shared " +
                        "software: other programs may already depend on it.",
                        isReversible: false);
                }
                else
                {
                    failedActions++;
                    hasRunError = true;
                    JournalFail(dependencyStep, "The install command did not succeed.");
                }

                await RefreshEnvironmentDiagnosticsAsync(token);
            }

            // The package goes last: prerequisites must be in place first, and a failure
            // here must not leave half-installed dependencies unexplained.
            await InstallPackageAsync(report, token);

            // Deliberately after the package, never before it, and never at all when there is
            // no installation to write into. See ResidencyPolicyWriter for why this write is
            // the one that used to poison every later installation on a clean machine.
            ApplyResidencyPolicy(report);

            // Beside the residency policy and for the same reason: after the package, so the
            // runtime directory exists with the permissions an installation gives it.
            await RecordOllamaLaunchPathAsync(report, token);

            // After the package, for the same reason the models are: the registration points
            // at the launcher this run installed, so writing it before the launcher exists
            // would hand every client a path to nothing.
            await ApplyAgentConfigurationAsync(report, token);

            SetProgress(95, "Finalising...");
            AppendLog(report, "Finalising.");
            finish.Summary = BuildFinishSummary(
                requested,
                successfulActions,
                skippedActions,
                failedActions);
            SetProgress(100, hasRunError ? "Failed" : "Completed");
            isComplete = !hasRunError;
            JournalFinish(hasRunError
                ? InstallerRunOutcome.Failed
                : InstallerRunOutcome.Completed);
            AppendRollbackAvailability(report);
            finish.Progress = report.ToString().Trim();
            SetRunLog(report.ToString(), false);
            CurrentPage = InstallerPage.Finish;
            return !hasRunError;
        }
        catch (OperationCanceledException)
        {
            AppendLog(report, "Cancelled. Actions already applied were left in place.");
            JournalFinish(InstallerRunOutcome.Cancelled);
            AppendRollbackAvailability(report);
            wasCancelled = true;
            hasRunError = true;
            isComplete = false;
            SetProgress(progress, "Cancelled");
            finish.Summary = BuildFinishSummary(
                requested,
                successfulActions,
                skippedActions,
                failedActions,
                "Cancelled by the user.");
            finish.Progress = report.ToString().Trim();
            CurrentPage = InstallerPage.Finish;
            return false;
        }
        catch (Exception exception)
        {
            AppendLog(report, $"Failed: {exception.Message}");
            JournalFinish(InstallerRunOutcome.Failed);
            AppendRollbackAvailability(report);
            hasRunError = true;
            isComplete = false;
            SetProgress(100, "Failed");
            finish.Summary = BuildFinishSummary(
                requested,
                successfulActions,
                skippedActions,
                failedActions,
                exception.Message);
            finish.Progress = report.ToString().Trim();
            SetRunLog(report.ToString(), false);
            CurrentPage = InstallerPage.Finish;
            return false;
        }
        finally
        {
            isRunning = false;
            runCancellation = null;
            PersistReport(report);
            RefreshAll();
        }
    }

    /// <summary>
    /// The run report only ever existed inside this window, so a refused installation explained
    /// itself exactly once and lost the explanation the moment the wizard was closed - which is
    /// precisely when someone needs it. Persisting it turns "it did not install" into a reason.
    ///
    /// Written outside the LocalAi root deliberately. That tree is validated against an exact
    /// name list on every install, so a log directory inside it would refuse the next
    /// installation rather than help diagnose the last one.
    /// </summary>
    private void PersistReport(StringBuilder report)
    {
        if (report is null || report.Length == 0)
        {
            return;
        }

        try
        {
            var directory = LogDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, report.ToString());
            finish.Summary = string.IsNullOrWhiteSpace(finish.Summary)
                ? $"Report saved to {path}."
                : $"{finish.Summary}{Environment.NewLine}Report saved to {path}.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException)
        {
            // A missing log must never turn a successful install into a failed one.
        }
    }

    /// <summary>
    /// Journal writes never fail the installation they describe: a run that applied its
    /// effects but could not write about them is still a run that applied its effects.
    /// A write failure silently drops the journal, and with it the rollback offer, so the
    /// finish page can only promise what the record can actually deliver.
    /// </summary>
    private string? JournalBegin(InstallerRunEffectKind kind, string description)
    {
        if (runJournal is not { } journal)
        {
            return null;
        }

        try
        {
            return journal.BeginStep(kind, description);
        }
        catch (Exception exception) when (IsJournalWriteFailure(exception))
        {
            runJournal = null;
            return null;
        }
    }

    private void JournalComplete(
        string? stepId,
        string detail,
        bool isReversible,
        InstallerRunUndoData? undo = null)
    {
        if (stepId is null || runJournal is not { } journal)
        {
            return;
        }

        try
        {
            journal.CompleteStep(stepId, detail, isReversible, undo);
        }
        catch (Exception exception) when (IsJournalWriteFailure(exception))
        {
            runJournal = null;
        }
    }

    private void JournalFail(string? stepId, string detail)
    {
        if (stepId is null || runJournal is not { } journal)
        {
            return;
        }

        try
        {
            journal.FailStep(stepId, detail);
        }
        catch (Exception exception) when (IsJournalWriteFailure(exception))
        {
            runJournal = null;
        }
    }

    private void JournalFinish(InstallerRunOutcome outcome)
    {
        if (runJournal is not { } journal)
        {
            return;
        }

        try
        {
            journal.Finish(outcome);
        }
        catch (Exception exception) when (IsJournalWriteFailure(exception))
        {
            runJournal = null;
        }
    }

    private static bool IsJournalWriteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or InvalidOperationException;

    /// <summary>
    /// One line at the end of a failed or cancelled run saying whether anything can be
    /// taken back. Without it "Cancelled. Actions already applied were left in place." was
    /// the entire recovery story, and the Roll back button below the log had no explanation.
    /// </summary>
    private void AppendRollbackAvailability(StringBuilder report)
    {
        if (runJournal is not { } journal || journal.Snapshot.Outcome is
            not (InstallerRunOutcome.Failed or InstallerRunOutcome.Cancelled))
        {
            return;
        }

        AppendLog(
            report,
            journal.Snapshot.HasReversibleWork
                ? "Some of the applied actions are reversible. Use \"Roll back changes\" " +
                    "below to undo them; the journal at " + journal.JournalPath +
                    " records exactly what was done."
                : "Nothing this run applied is reversible by the installer. The journal " +
                    "at " + journal.JournalPath + " records exactly what was done.");
    }

    public async Task RollbackThisRunAsync()
    {
        if (!CanRollback || runJournal is not { } journal)
        {
            return;
        }

        var rollbackReport = await ExecuteRollbackAsync(journal);
        finish.RollbackReport = rollbackReport;
        RefreshAll();
    }

    public async Task RollbackPreviousRunAsync()
    {
        if (interruptedJournal is not { } journal || isRunning)
        {
            return;
        }

        var rollbackReport = await ExecuteRollbackAsync(journal);
        // The journal now carries a rollback outcome, so it is no longer "interrupted" and
        // the next start will not ask about it. The notice space shows the result instead.
        interruptedJournal = null;
        interruptedRunNotice = rollbackReport;
        RefreshAll();
    }

    private async Task<string> ExecuteRollbackAsync(InstallerRunJournal journal)
    {
        isRunning = true;
        RefreshAll();
        try
        {
            var rollback = new InstallerRunRollback(
                processRunner,
                InstallationLayout.CreateDefault(),
                RollbackActivationTimeout);
            return FormatRollbackReport(
                await rollback.RollbackAsync(journal, CancellationToken.None));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"Rollback failed: {exception.Message} The journal at " +
                $"{journal.JournalPath} still records what the run did.";
        }
        finally
        {
            isRunning = false;
            RefreshAll();
        }
    }

    /// <summary>
    /// Undone, left in place, failed — by name, effect by effect. The finish page used to
    /// show "RollbackNotes" that were only ever the run log; this text exists so the page
    /// never again implies more was undone than actually was.
    /// </summary>
    private static string FormatRollbackReport(InstallerRollbackReport rollbackResult)
    {
        var text = new StringBuilder();
        text.AppendLine(rollbackResult.AllReversibleUndone
            ? "Rollback finished. Effect by effect:"
            : "Rollback finished with failures. Effect by effect:");
        foreach (var step in rollbackResult.Steps)
        {
            var verdict = step.Outcome switch
            {
                InstallerRollbackStepOutcome.Undone => "undone",
                InstallerRollbackStepOutcome.LeftInPlace => "left in place",
                InstallerRollbackStepOutcome.Skipped => "left alone",
                _ => "rollback FAILED",
            };
            text.AppendLine($"  - {step.Description}: {verdict}. {step.Detail}");
        }

        return text.ToString().TrimEnd();
    }

    public void SetProgress(int value, string message)
    {
        progress = Math.Clamp(value, 0, 100);
        progressText = message;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    public void SetRunLog(string message, bool requiresRestart)
    {
        runLogMessage = message;
        finish.RequiresRestart = requiresRestart;
        finish.RunLog = message;
        OnPropertyChanged(nameof(RunLog));
    }

    public void RefreshNavigationState() => RefreshAll();

    /// <summary>
    /// Registers the MCP servers and writes the managed instruction block for every client
    /// the user chose to configure.
    ///
    /// This step existed only as a page for a while: the wizard collected the choice, printed
    /// it on the review screen, and then never acted on it, so a finished installation left
    /// Claude and Codex unable to reach anything it had installed. Each client is applied
    /// independently — a malformed config in one must not cost the other its integration.
    /// </summary>
    private async Task ApplyAgentConfigurationAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        var requested = agents.Agents
            .Where(agent => agent.Choice != AgentChoice.NoChange)
            .ToArray();
        if (requested.Length == 0)
        {
            AppendLog(report, "Client applications: left unchanged.");
            return;
        }

        if (packageOutcome is null || !packageInstalled)
        {
            AppendLog(
                report,
                "Client applications: skipped, because the LocalAi package was not installed " +
                "and the registration would point at a launcher that is not there.");
            hasRunError = true;
            return;
        }

        SetProgress(94, "Configuring client applications...");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binRoot = InstallationLayout.CreateDefault().BinRoot;
        foreach (var agent in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isClaude = string.Equals(agent.Agent, "claude", StringComparison.OrdinalIgnoreCase);
            string? agentStep = null;
            try
            {
                var claude = isClaude ? new ClaudeConfigurationAdapter(home, binRoot) : null;
                var codex = isClaude ? null : new CodexConfigurationAdapter(home, binRoot);
                var choice = agent.Choice.ToCore();
                var plan = claude is not null
                    ? claude.Preview(choice)
                    : codex!.Preview(choice);
                if (!plan.HasChanges)
                {
                    AppendLog(report, $"{plan.AgentName}: already configured, nothing to change.");
                    continue;
                }

                agentStep = JournalBegin(
                    InstallerRunEffectKind.AgentConfiguration,
                    $"{plan.AgentName} client configuration");
                if (claude is not null)
                {
                    await claude.ApplyAsync(plan, cancellationToken);
                }
                else
                {
                    await codex!.ApplyAsync(plan, cancellationToken);
                }

                JournalComplete(
                    agentStep,
                    "Applied to " +
                    string.Join(", ", plan.Files.Select(file => file.Path)) + ".",
                    isReversible: true,
                    new InstallerRunUndoData(
                        Files: plan.Files.Select(BuildAgentFileUndo).ToArray()));
                AppendLog(
                    report,
                    $"{plan.AgentName}: {agent.Choice.Title()} applied to " +
                    string.Join(", ", plan.Files.Select(file => file.Path)) +
                    ". Restart the client to pick it up.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A hand-edited config that the adapter refuses to rewrite is the common case
                // here, and it is recoverable by hand, so it must not fail the installation.
                // The adapter also restores its own partial writes before throwing, which is
                // why a failed step leaves nothing for rollback to undo.
                JournalFail(agentStep, exception.Message);
                AppendLog(
                    report,
                    $"{agent.DisplayName}: not configured — {exception.Message} " +
                    "The rest of the installation is unaffected.");
                hasRunError = true;
            }
        }
    }

    /// <summary>
    /// The restore source is the .bak file the adapter already writes beside the config; a
    /// backup exists exactly when the file existed before the run, so its presence after a
    /// successful apply doubles as the existed-before record. The journal keeps hashes for
    /// both sides so rollback can prove the backup is the pre-install content and that the
    /// file has not been hand-edited since the run.
    /// </summary>
    private static InstallerRunFileUndo BuildAgentFileUndo(AgentConfigurationFilePlan file)
    {
        var backupExists = File.Exists(file.BackupPath);
        return new(
            file.Path,
            backupExists,
            file.ExpectedSha256,
            null,
            backupExists ? file.BackupPath : null,
            file.AfterSha256);
    }

    /// <summary>
    /// Records the Ollama the installer verified, so the broker can start it when a model call
    /// finds it down instead of leaving the work waiting for somebody to notice.
    ///
    /// Only the validated answer is recorded. The detector falls back to a plain PATH lookup
    /// when the uninstall entry does not match, and a path resolved that way is exactly what a
    /// background process must never launch unattended -- the search path is writable by the
    /// user. No record is better than a record of the wrong file.
    /// </summary>
    private async Task RecordOllamaLaunchPathAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        string? recordStep = null;
        try
        {
            var installed = await installedApplications.FindOllamaAsync(cancellationToken);
            if (installed?.ExecutablePath is not { Length: > 0 } path)
            {
                AppendLog(
                    report,
                    "Ollama start-on-demand: not recorded, because no verified installation of "
                    + "Ollama was found. A model call will say Ollama is not answering rather "
                    + "than starting it.");
                return;
            }

            var recordPath = Path.Combine(
                ModelResidencyPolicyStore.DefaultRuntimeRoot,
                OllamaLaunchRecord.FileName);
            var priorRecord = CaptureFileState(recordPath);
            recordStep = JournalBegin(
                InstallerRunEffectKind.OllamaLaunchRecord,
                "Ollama start-on-demand record");
            new OllamaLaunchRecordStore(ModelResidencyPolicyStore.DefaultRuntimeRoot)
                .Save(path, installed.DetectedVersion);
            JournalComplete(
                recordStep,
                $"Wrote {recordPath}.",
                isReversible: true,
                new InstallerRunUndoData(Files: [BuildFileUndo(recordPath, priorRecord)]));
            AppendLog(report, $"Ollama start-on-demand: recorded {path}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            JournalFail(recordStep, exception.Message);
            AppendLog(
                report,
                $"Ollama start-on-demand: not recorded ({exception.Message}).");
        }
    }

    private void ApplyResidencyPolicy(StringBuilder report)
    {
        string? residencyStep = null;
        try
        {
            var runtimeRoot = ModelResidencyPolicyStore.DefaultRuntimeRoot;
            var policyPath = Path.Combine(runtimeRoot, BrokerPolicy.FileName);
            // Captured before the write, journalled only when the writer will actually
            // write: mirroring its own installation check keeps the journal free of steps
            // for effects that never happened.
            var priorPolicy = Directory.Exists(runtimeRoot)
                ? CaptureFileState(policyPath)
                : null;
            if (priorPolicy is not null)
            {
                residencyStep = JournalBegin(
                    InstallerRunEffectKind.ResidencyPolicy,
                    $"Model residency policy ({residency.Policy})");
            }

            var outcome = ResidencyPolicyWriter.Apply(runtimeRoot, residency.Policy);
            if (outcome == ResidencyPolicyOutcome.SkippedWithoutInstallation)
            {
                JournalFail(residencyStep, "Nothing was written: LocalAi is not installed.");
                AppendLog(
                    report,
                    "Model residency policy: not stored, because LocalAi is not installed on " +
                    "this computer. Storing it would create the LocalAi directory without the " +
                    "permissions an installation requires, and every later installation would " +
                    "then refuse it. The next run asks for this setting again.");
                return;
            }

            JournalComplete(
                residencyStep,
                $"Wrote {policyPath}.",
                isReversible: true,
                priorPolicy is { } captured
                    ? new InstallerRunUndoData(Files: [BuildFileUndo(policyPath, captured)])
                    : null);
            AppendLog(report, $"Model residency policy: {residency.Policy}.");
            if (residency.Policy != ModelResidencyPolicy.RequireFullVram)
            {
                AppendLog(
                    report,
                    "Warning: residency is relaxed, so models may be slower than a fully " +
                    "resident load. Degraded answers will say so.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            JournalFail(residencyStep, exception.Message);
            AppendLog(report, $"Could not store the residency policy: {exception.Message}");
            hasRunError = true;
        }
    }

    /// <summary>The pre-effect content of a file the run is about to change, or its absence.</summary>
    private sealed record CapturedFileState(bool Existed, byte[] Bytes);

    private static CapturedFileState CaptureFileState(string path) =>
        File.Exists(path)
            ? new(true, File.ReadAllBytes(path))
            : new(false, []);

    /// <summary>
    /// Pairs the captured pre-effect content with what is on disk now. Small files travel
    /// inline in the journal; anything above the cap keeps only its hash, which rollback
    /// reports as unrecoverable instead of guessing.
    /// </summary>
    private static InstallerRunFileUndo BuildFileUndo(string path, CapturedFileState before)
    {
        var after = File.Exists(path) ? File.ReadAllBytes(path) : [];
        return new(
            path,
            before.Existed,
            InstallerRunJournal.Sha256Hex(before.Bytes),
            before.Existed && before.Bytes.Length <= InstallerRunJournal.MaximumInlineContentBytes
                ? Convert.ToBase64String(before.Bytes)
                : null,
            null,
            InstallerRunJournal.Sha256Hex(after));
    }

    private static int StepIndex(InstallerPage page)
    {
        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].Page == page)
            {
                return index;
            }
        }

        return 0;
    }

    private string BuildReview()
    {
        var builder = new StringBuilder();
        builder.AppendLine(package.ReviewText);
        builder.AppendLine(dependencies.ReviewText);
        builder.AppendLine(models.ReviewText);
        builder.AppendLine(residency.ReviewText);
        builder.AppendLine(agents.ReviewText);
        if (residency.HasWarning)
        {
            builder.AppendLine();
            builder.AppendLine("Warning: " + residency.Warning);
        }

        // The package is the point of the run, and its absence is the one line on this page a
        // reader skims past — "not resolved" sits in a list of five neutral statements. An
        // installation that goes ahead without it leaves the clients unconfigured too, so it
        // is worth saying twice, in the register the rest of the page reserves for warnings.
        if (!package.HasPackage)
        {
            builder.AppendLine();
            builder.AppendLine(
                "Warning: no release has been verified, so LocalAi itself will not be " +
                "installed and the client applications will be left unconfigured. Only the " +
                "prerequisites above will be applied. Go back to the LocalAi package step to " +
                "check a release first.");
        }

        return builder.ToString().Trim();
    }

    private void RebuildSteps()
    {
        StepList.Clear();
        var current = StepIndex(CurrentPage);
        for (var index = 0; index < Steps.Count; index++)
        {
            StepList.Add(new WizardStep(
                Steps[index].Page,
                Steps[index].Title,
                index == current,
                index < current));
        }
    }

    private void RefreshAll()
    {
        RebuildSteps();
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepDescription));
        OnPropertyChanged(nameof(StepStatus));
        OnPropertyChanged(nameof(StepList));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsNextVisible));
        OnPropertyChanged(nameof(IsInstallVisible));
        OnPropertyChanged(nameof(CancelButtonText));
        OnPropertyChanged(nameof(IsFinishPage));
        OnPropertyChanged(nameof(IsProgressPage));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(HasRunError));
        OnPropertyChanged(nameof(ReviewText));
        OnPropertyChanged(nameof(FinishSummary));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(HasInterruptedRun));
        OnPropertyChanged(nameof(InterruptedRunNotice));
        OnPropertyChanged(nameof(HasInterruptedRunNotice));
        BackCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RollbackCommand.RaiseCanExecuteChanged();
        RollbackPreviousRunCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Appends a line and publishes it immediately.
    ///
    /// The log used to be assigned only after the run finished, so the progress page stayed
    /// blank for the whole installation — including the minutes spent downloading — and gave
    /// no sign that anything was happening.
    /// </summary>
    private void AppendLog(StringBuilder report, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        report.AppendLine(message);
        finish.Progress = report.ToString().TrimEnd();
    }

    private string BuildFinishSummary(
        int requestedActions,
        int successfulActions,
        int skippedActions,
        int failedActions,
        string? fatalMessage = null)
    {
        var summary = new StringBuilder();
        // The package is the point of the whole run, so it is reported first and by name.
        // The counters below cover prerequisites only, and on their own they read as "0, 0,
        // 0, 0" even when the package failed.
        summary.AppendLine($"LocalAi package: {packageOutcome ?? "not attempted"}.");
        summary.AppendLine();
        summary.AppendLine($"Prerequisites requested: {requestedActions}.");
        summary.AppendLine($"Installed: {successfulActions}.");
        summary.AppendLine($"Skipped: {skippedActions}.");
        summary.AppendLine($"Failed: {failedActions}.");
        if (!string.IsNullOrWhiteSpace(fatalMessage))
        {
            summary.AppendLine(fatalMessage);
        }

        return summary.ToString().Trim();
    }

    private async Task RefreshEnvironmentDiagnosticsAsync(CancellationToken cancellationToken)
    {
        diagnose.IsChecking = true;
        RefreshAll();
        try
        {
            await RunEnvironmentDiagnosticsAsync(cancellationToken);
        }
        finally
        {
            diagnose.IsChecking = false;
            RefreshAll();
        }
    }

    private async Task RunEnvironmentDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var diagnosis = await environmentDetector.DetectAsync(cancellationToken);
        environmentDiagnosis = diagnosis;

        diagnose.Load(diagnosis);
        residency.HasUsableAdapter = diagnose.HasUsableAdapter;
        agents.ApplyDetection(diagnosis.Agents);
        await RefreshRecommendationAsync(diagnosis, cancellationToken);

        // Detection records presence only; it must not grant consent on the user's behalf.
        dependencies.SetInstalled("Git", diagnosis.Git.State == DependencyState.Detected);
        dependencies.SetInstalled("Ollama", diagnosis.Ollama.State == DependencyState.Detected);
        dependencies.SetInstalled(
            "GitHubCli",
            diagnosis.GitHubCli.State == DependencyState.Detected);
        dependencies.SetInstalled(
            "DotNetSdk",
            diagnosis.DotNetSdk.State == DependencyState.Detected);
        dependencies.SetInstalled("NodeJs", diagnosis.NodeJs.State == DependencyState.Detected);
        dependencies.SetInstalled(
            "ScipTypeScript",
            diagnosis.ScipTypeScript.State == DependencyState.Detected);
        dependencies.SetInstalled("Python", diagnosis.Python.State == DependencyState.Detected);
        dependencies.SetInstalled(
            "ScipPython",
            diagnosis.ScipPython.State == DependencyState.Detected);

        OnPropertyChanged(nameof(Diagnose));
        OnPropertyChanged(nameof(Dependencies));
        RefreshAll();
    }

    /// <summary>
    /// Recomputes which catalogue models fit this machine. Sizes come from the public model
    /// registry, so this is a network call: it must never prevent the wizard from running,
    /// and an offline machine simply gets the catalogue without sizes.
    /// </summary>
    private async Task RefreshRecommendationAsync(
        EnvironmentDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        try
        {
            using var registry = new OllamaRegistryClient();
            var recommender = new CatalogModelRecommender(registry);
            lastRecommendation = await recommender.RecommendAsync(
                diagnosis.Gpu,
                models.CatalogModels,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lastRecommendation = CatalogRecommendation.Empty;
        }

        models.ApplyRecommendation(
            lastRecommendation,
            residency.Policy == ModelResidencyPolicy.RequireFullVram);
    }

    /// <summary>
    /// Re-applies the last recommendation under the current residency choice, so relaxing
    /// the policy immediately widens what the models page offers.
    /// </summary>
    public void OnResidencyChanged()
    {
        models.ApplyRecommendation(
            lastRecommendation,
            residency.Policy == ModelResidencyPolicy.RequireFullVram);
        RefreshAll();
    }

    /// <summary>
    /// The GitHub CLI exactly where the environment check found it, or null if it found nothing.
    ///
    /// Resolving it once and reusing the answer is the point. The feed defaults to the bare name
    /// "gh" and leaves the lookup to CreateProcess, which searches only the PATH this process
    /// inherited at startup — so on a machine where the CLI was installed during the session the
    /// check reported it present, from the registry, and the very next step still failed with
    /// "The GitHub CLI could not be started". Two lookups with different rules cannot disagree
    /// if there is only one.
    /// </summary>
    private string? GitHubCliPath =>
        environmentDiagnosis?.GitHubCli is
            { State: DependencyState.Detected, ExecutablePath: { } path }
            ? path
            : null;

    /// <summary>
    /// The release feed for this machine: anonymous HTTPS first, the GitHub CLI behind it.
    ///
    /// The repository is public, so an installation must not begin by demanding an account —
    /// that used to be three obstacles (an account, an invitation, `gh auth login`) in front
    /// of a download anyone is allowed to make. The CLI is still offered to the chain when it
    /// is installed and signed in, because a fork kept private stays installable that way,
    /// and because a network that blocks the release host may not block the API.
    ///
    /// Neither path is trusted for being itself: the manifest is checked against the embedded
    /// key and the package against the hash in that manifest, whichever one fetched them.
    /// </summary>
    /// <remarks>
    /// A chosen folder replaces the chain rather than joining it. Falling back to GitHub after a
    /// folder failed would be the opposite of what asking for a folder means — on an air-gapped
    /// machine it turns one clear error into a long timeout, and on any machine it would install
    /// something other than what the operator pointed at.
    /// </remarks>
    private IReleaseFeed CreateFeed() =>
        string.IsNullOrWhiteSpace(package.SourceFolder)
            ? new FallbackReleaseFeed(
                anonymousFeed,
                GitHubCliPath is null
                    ? null
                    : new GitHubReleaseFeed(processRunner, gitHubCliPath: GitHubCliPath))
            : new DirectoryReleaseFeed(package.SourceFolder);

    private static DependencyDefinition? ResolveDependencyDefinition(string dependencyId) =>
        dependencyId switch
        {
            "Git" => DependencyCatalog.Git,
            "Ollama" => DependencyCatalog.Ollama,
            "GitHubCli" => DependencyCatalog.GitHubCli,
            "DotNetSdk" => DependencyCatalog.DotNetSdk,
            "NodeJs" => DependencyCatalog.NodeJs,
            "ScipTypeScript" => DependencyCatalog.ScipTypeScript,
            "Python" => DependencyCatalog.Python,
            "ScipPython" => DependencyCatalog.ScipPython,
            _ => null,
        };

    /// <summary>
    /// Scratch space for downloaded release assets.
    ///
    /// Deliberately outside the installation layout. It first lived in
    /// %LOCALAPPDATA%\LocalAi\installer\downloads, which looked tidy and broke every
    /// install: that directory belongs to the installer and must contain exactly "backups"
    /// and "transaction.lock", so an extra folder made the layout unrecognisable to the
    /// installer's own validation — after the package had already been downloaded.
    /// </summary>
    private static string WorkingDirectory => Path.Combine(
        Path.GetTempPath(),
        "LocalAi-installer");

    /// <summary>
    /// Resolves and verifies the requested release. Failures are shown on the package page
    /// rather than thrown: an unresolved package does not stop the rest of the installation.
    /// </summary>
    public async Task ResolvePackageAsync(CancellationToken cancellationToken = default)
    {
        package.ReportUnavailable("Checking the release...");
        RefreshAll();
        try
        {
            var feed = CreateFeed();
            package.InstalledVersionDirectory = InstalledVersionDirectory();
            // "latest" is not a tag GitHub knows; resolve it to the newest published one so
            // the field can keep its convenient default.
            resolvedTag = await feed.ResolveTagAsync(package.ReleaseVersion, cancellationToken);
            package.SelectResolvedRelease(
                await feed.ResolveAsync(resolvedTag, WorkingDirectory, cancellationToken),
                resolvedTag);
        }
        catch (ReleaseResolutionException exception)
        {
            package.ReportUnavailable(exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            package.ReportUnavailable($"The release could not be checked: {exception.Message}");
        }

        RefreshAll();
    }

    /// <summary>
    /// The version directory currently active, or null when nothing is installed or the
    /// pointer cannot be read. Only used to tell the user a release is already installed, so a
    /// failure here costs a warning, not the run.
    /// </summary>
    private static string? InstalledVersionDirectory()
    {
        try
        {
            var snapshot = new ExistingLocalAiInspector(new SystemFileSystemProbe())
                .Inspect(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData));
            return snapshot.Version;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-resolves a request left at "latest" immediately before installing it.
    ///
    /// The package page checks the release when it is opened, and a wizard can sit on the
    /// confirmation step for a long time — long enough for a release to be published in
    /// between. Installing what was newest when the window opened, under a field that still
    /// says "latest", is the one outcome that word rules out. A specific tag is left alone: it
    /// was asked for by name.
    /// </summary>
    private async Task RefreshLatestBeforeInstallAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        if (!package.WantsLatest)
        {
            return;
        }

        try
        {
            var feed = CreateFeed();
            var newest = await feed.ResolveTagAsync(
                PackagePageViewModel.LatestTag,
                cancellationToken);
            if (string.Equals(newest, package.ResolvedTag, StringComparison.Ordinal))
            {
                return;
            }

            AppendLog(
                report,
                $"LocalAi package: {newest} was published after this release was checked; " +
                "resolving it instead.");
            resolvedTag = newest;
            package.SelectResolvedRelease(
                await feed.ResolveAsync(newest, WorkingDirectory, cancellationToken),
                newest);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Whatever was already verified stays selected: a feed that cannot be reached right
            // now is a reason to install what was checked, not to abandon the run.
            AppendLog(
                report,
                "LocalAi package: the newest release could not be re-checked " +
                $"({exception.Message}); continuing with {package.ResolvedTag}.");
        }
    }

    private async Task InstallPackageAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        await RefreshLatestBeforeInstallAsync(report, cancellationToken);
        if (package.Resolved is not { } resolved)
        {
            // Not a skip: installing the package is the entire point of this wizard. Returning
            // quietly here let the run finish as "Installation complete" while nothing had been
            // installed at all, which is the one outcome worse than a visible failure.
            packageOutcome = "not installed, no verified release was selected";
            AppendLog(
                report,
                "LocalAi package: no verified release was selected, so nothing was installed. " +
                "Return to the package step and check the release before continuing.");
            hasRunError = true;
            return;
        }

        AppendLog(
            report,
            $"LocalAi package {resolved.Manifest.ReleaseVersion}: downloading and verifying...");

        // Real byte progress: the expected size comes from the verified manifest, and the
        // downloaded bytes are observed as the file grows.
        var total = resolved.Manifest.PackageSize;
        // Progress callbacks are posted, not called, so the last download report can land after
        // the model phase has already begun. Without this the bar sat at "downloading 208 of
        // 208 MB" for the whole of a two-minute preflight, which reads as a hung installer with
        // nothing to press but Cancel.
        var downloadFinished = false;
        var downloadProgress = new Progress<long>(bytes =>
        {
            if (downloadFinished)
            {
                return;
            }

            SetProgress(
                40 + (int)(50 * Math.Clamp(bytes, 0, total) / Math.Max(total, 1)),
                $"Downloading the LocalAi package: " +
                $"{bytes / (1024d * 1024):N0} of {total / (1024d * 1024):N0} MB");
        });
        SetProgress(40, "Downloading the LocalAi package...");

        var service = new ReleaseInstallService(
            CreateFeed(),
            processRunner,
            new SystemFileSystemProbe());
        var modelProgress = new Progress<ModelProvisioningProgress>(step =>
        {
            downloadFinished = true;
            // 90 to 95: the package is in, the models are the tail of the run. The count is
            // what makes a long wait legible — "3 of 6" is a queue, a frozen bar is a fault.
            var share = step.Total <= 0 ? 0 : 5 * step.Completed / step.Total;
            SetProgress(
                90 + share,
                step.Total > 0
                    ? $"Local models: {step.Completed} of {step.Total} — {step.Message}"
                    : step.Message);
            AppendLog(report, step.Message);
        });
        var selection = models.BuildProvisioningSelection();
        // The intent covers the download too, but the download only touches the temp
        // directory; the effect the journal exists for is the activation. The callback
        // below closes this step at the exact moment activation finishes, so a process
        // killed during the model pulls that follow leaves the activation recorded as done
        // rather than as "state unknown".
        var packageStep = JournalBegin(
            InstallerRunEffectKind.PackageActivation,
            $"LocalAi package {resolved.Manifest.ReleaseVersion}");
        string? modelsStep = null;
        ReleaseInstallResult result;
        try
        {
            result = await service.InstallAsync(
                resolved,
                WorkingDirectory,
                resolvedTag ?? resolved.Manifest.ReleaseVersion,
                downloadProgress,
                selection,
                environmentDiagnosis?.Gpu,
                modelProgress,
                activation =>
                {
                    JournalPackageActivation(packageStep, activation);
                    if (activation.Installed && selection.Mode != ModelProvisioningMode.None)
                    {
                        modelsStep = JournalBegin(
                            InstallerRunEffectKind.ModelInstall,
                            "Local models through the broker");
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            JournalFail(modelsStep ?? packageStep, "Cancelled before it could finish.");
            throw;
        }
        catch (LocalAiPackageInstallationException exception)
        {
            JournalFail(packageStep, exception.Message);
            // A refused layout is the one installation failure a user can actually fix, and
            // the refusal alone never says how. Letting it reach the outer handler produced
            // "Failed: The LocalAi installation layout is unsafe (check: ValidateAcl)" and
            // nothing else — accurate, unactionable, and indistinguishable from a product
            // defect. It is caught here so the advice can name the directory and say what is
            // in it, and so the run still finishes its own reporting instead of aborting.
            packageInstalled = false;
            packageOutcome = $"not installed — {exception.Message}";
            AppendLog(report, $"LocalAi package: {exception.Message}");
            var layout = InstallationLayout.CreateDefault();
            var advice = InstallationFailureAdvice.ForLayoutFailure(
                exception.Message,
                layout.Root,
                HoldsInstalledVersions(layout));
            if (advice is not null)
            {
                AppendLog(report, advice);
            }

            hasRunError = true;
            return;
        }

        SetProgress(90, "Installing the LocalAi package...");

        packageInstalled = result.Installed;
        packageOutcome = result.Installed
            ? $"{result.Status}, version {result.Version}"
            : $"{result.Status} — {result.Reason}".TrimEnd(' ', '—');
        AppendLog(
            report,
            result.Installed
                ? $"LocalAi package: {result.Status}, version {result.Version} at {result.VersionPath}."
                : $"LocalAi package: {result.Status}. {result.Reason}".Trim());
        if (!result.Installed)
        {
            hasRunError = true;
        }

        ReportModelOutcome(report, result.Models, selection);
        if (modelsStep is not null)
        {
            var pulled = result.Models?.Batch?.Models
                .Count(model => model.PullCompleted) ?? 0;
            JournalComplete(
                modelsStep,
                $"Models newly downloaded: {pulled}. Pulled models live in Ollama's own " +
                "store, shared with everything else that uses it, and are not removed by " +
                "this installer.",
                isReversible: false);
        }
    }

    /// <summary>
    /// What activation left behind decides what rollback can promise. Only an upgrade is
    /// reversible: the launcher can reactivate the version that was current before. A first
    /// installation has nothing to return to, and its root starts holding runtime data —
    /// indexes included — the moment the broker runs, so "undo" would mean deleting things
    /// this run did not create.
    /// </summary>
    private void JournalPackageActivation(string? packageStep, ReleaseInstallResult activation)
    {
        if (!activation.Installed)
        {
            // The package installer recovers its own failures: by the time it reports
            // anything but success, the prior state is back (or the reason says why not).
            JournalFail(
                packageStep,
                $"{activation.Status}. {activation.Reason}".Trim());
            return;
        }

        var isUpgrade = activation.PriorVersion is { Length: > 0 } prior &&
            !string.Equals(prior, activation.Version, StringComparison.Ordinal);
        JournalComplete(
            packageStep,
            isUpgrade
                ? $"Activated {activation.Version}; {activation.PriorVersion} was active " +
                    "before and can be reactivated."
                : activation.PriorVersion is null
                    ? $"First installation of {activation.Version}: there is no previous " +
                        "version to return to, and the LocalAi directory starts holding " +
                        "runtime data as soon as it is used."
                    : $"Version {activation.Version} was already active.",
            isReversible: isUpgrade,
            isUpgrade
                ? new InstallerRunUndoData(activation.Version, activation.PriorVersion)
                : null);
    }

    /// <summary>
    /// Whether the LocalAi root holds an installed version. Only used to choose which advice
    /// to print after a refused layout, so an unreadable directory answers "assume it does":
    /// advising someone to delete a tree that might hold their indexes has to be the answer
    /// this is sure about, never the one it guesses.
    /// </summary>
    private static bool HoldsInstalledVersions(InstallationLayout layout)
    {
        try
        {
            return Directory.Exists(layout.VersionsRoot) &&
                Directory.EnumerateFileSystemEntries(layout.VersionsRoot).Any();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// A model that was asked for and did not arrive has to be visible here. The broker
    /// answers per model, and a refusal carries the smaller context sizes that would have
    /// fitted, so both are printed rather than collapsed into "models: failed".
    /// </summary>
    private void ReportModelOutcome(
        StringBuilder report,
        ReleaseModelInstallReport? models,
        ModelProvisioningSelection selection)
    {
        if (models?.Batch is not { } batch)
        {
            // No batch means the planner produced no request at all. When models were asked
            // for, that is a failure of the run, not an absence of news — and it used to be
            // reported as neither. A release signed without a model list, or a machine no
            // catalogue model fits, ended as "Installation complete" with one explanatory
            // line buried in the log, and the first sign of trouble was CodeSearch having no
            // embedding model days later.
            if (selection.Mode == ModelProvisioningMode.None || models is null)
            {
                return;
            }

            var reasons = models.Excluded.Count == 0
                ? "The installer produced no model request."
                : string.Join(" ", models.Excluded);
            AppendLog(
                report,
                "Local models: none were installed. " + reasons +
                " Nothing else in this installation is affected, but local model work — " +
                "CodeSearch indexing included — cannot run until at least one model is " +
                "present. Ask the client to run the 'local_models_sync' tool, or install " +
                "one from a release whose manifest carries the model list.");
            hasRunError = true;
            return;
        }

        foreach (var model in batch.Models)
        {
            var pulled = model.PullCompleted
                ? "downloaded"
                : model.PullAttempted
                    ? "download did not finish"
                    : "already present";
            if (model.Outcome == BrokerModelInstallOutcome.Accepted)
            {
                AppendLog(
                    report,
                    $"Model {model.Model} at {model.ContextTokens} tokens: {pulled}, " +
                    "fully resident.");
                continue;
            }

            var suggestion = model.FallbackSuggestions.Count == 0
                ? string.Empty
                : " Smaller options that fit: " +
                    string.Join(
                        ", ",
                        model.FallbackSuggestions.Select(fallback =>
                            $"{fallback.Model} at {fallback.ContextTokens}")) + ".";
            AppendLog(
                report,
                $"Model {model.Model} at {model.ContextTokens} tokens: {model.Outcome} " +
                $"({model.Code}), {pulled}.{suggestion}");
            hasRunError = true;
        }

        if (batch.StopReason != BrokerModelBatchStopReason.None)
        {
            AppendLog(
                report,
                $"Model setup stopped early: {batch.StopReason} ({batch.Code}).");
            hasRunError = true;
        }
    }

    private async Task<bool> TryInstallDependencyWithWingetAsync(
        DependencyDefinition dependency,
        string displayName,
        bool reinstall,
        CancellationToken cancellationToken)
    {
        if (environmentDiagnosis?.WinGet is not
            { State: DependencyState.Detected, ExecutablePath: { } wingetPath })
        {
            SetRunLog(
                $"WinGet is unavailable; install {displayName} manually from " +
                $"{dependency.OfficialInstallerUri}.",
                false);
            return false;
        }

        // Checked here rather than at detection, and before every install rather than once: the
        // detector reports the first file named winget.exe on a search path the user can write
        // to, and it reported it several pages ago. What runs is the path this check resolves.
        var winget = wingetSource.Authorize(wingetPath);
        if (!winget.Allowed)
        {
            SetRunLog(
                $"{displayName} was not installed. {winget.Message} You can install it " +
                $"yourself from {dependency.OfficialInstallerUri}.",
                false);
            return false;
        }

        var arguments = new List<string>
        {
            "install",
            "--id",
            dependency.PackageId,
            "--exact",
            "--source",
            "winget",
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
        };

        // --force only when the user explicitly asked to reinstall something already
        // present. Passing it unconditionally reinstalled working software on every run.
        if (reinstall)
        {
            arguments.Add("--force");
        }

        var result = await processRunner.RunAsync(
            winget.ExecutablePath,
            [.. arguments],
            DependencyInstallTimeout,
            cancellationToken);
        if (result.ExitCode is not 0)
        {
            SetRunLog(
                $"{displayName} installation failed with exit code {result.ExitCode}.",
                false);
            return false;
        }

        return true;
    }

    private async Task<bool> TryInstallDependencyWithNpmAsync(
        DependencyDefinition dependency,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (dependency.PackageVersion is not { Length: > 0 } packageVersion)
        {
            SetRunLog($"{displayName} has no pinned package version.", false);
            return false;
        }

        if (environmentDiagnosis?.Npm is not
            { State: DependencyState.Detected, ExecutablePath: { } npmPath })
        {
            SetRunLog(
                $"npm is unavailable; install Node.js 20 before {displayName}.",
                false);
            return false;
        }

        var result = await processRunner.RunAsync(
            npmPath,
            ["install", "--global", $"{dependency.PackageId}@{packageVersion}"],
            DependencyInstallTimeout,
            cancellationToken);
        if (result.ExitCode is not 0)
        {
            SetRunLog(
                $"{displayName} installation failed with exit code {result.ExitCode}.",
                false);
            return false;
        }

        if (string.Equals(
                dependency.PackageId,
                DependencyCatalog.ScipPython.PackageId,
                StringComparison.Ordinal))
        {
            var npmRootResult = await processRunner.RunAsync(
                npmPath,
                ["root", "--global"],
                DependencyInstallTimeout,
                cancellationToken);
            var npmGlobalRoot = npmRootResult.StandardOutput.Trim();
            if (npmRootResult.ExitCode is not 0 ||
                string.IsNullOrWhiteSpace(npmGlobalRoot) ||
                npmGlobalRoot.Contains('\n', StringComparison.Ordinal) ||
                npmGlobalRoot.Contains('\r', StringComparison.Ordinal))
            {
                SetRunLog(
                    "SCIP Python was installed, but npm did not return a valid global " +
                    "package directory.",
                    false);
                return false;
            }

            try
            {
                _ = new ScipPythonWindowsCompatibilityPatch().Apply(
                    npmGlobalRoot,
                    packageVersion);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or JsonException)
            {
                SetRunLog(
                    $"SCIP Python Windows compatibility patch failed: {exception.Message}",
                    false);
                return false;
            }

            var globalPrefix = Directory.GetParent(npmGlobalRoot)?.FullName;
            var scipPythonPath = globalPrefix is null
                ? null
                : Path.Combine(globalPrefix, "scip-python.cmd");
            if (scipPythonPath is null || !File.Exists(scipPythonPath))
            {
                SetRunLog(
                    "SCIP Python was patched, but its npm command shim was not found.",
                    false);
                return false;
            }

            var verification = await processRunner.RunAsync(
                scipPythonPath,
                ["--version"],
                DependencyInstallTimeout,
                cancellationToken);
            var versionOutput = verification.StandardOutput + verification.StandardError;
            if (verification.ExitCode is not 0 ||
                !versionOutput.Contains(packageVersion, StringComparison.Ordinal))
            {
                SetRunLog(
                    "SCIP Python was patched, but its executable verification failed.",
                    false);
                return false;
            }
        }

        return true;
    }
}
