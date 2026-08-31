using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The wizard in uninstall mode.
///
/// It is the installer's own executable rather than a CLI verb because everything removal
/// needs is already here — the client adapters, the managed-block machinery, the journal, the
/// review-page pattern — and because it runs from outside the tree it is deleting, which
/// spares it the self-deletion dance a `localai uninstall` would need on Windows.
///
/// The shape follows the installation exactly: choose, review everything on one page, confirm,
/// and only then act. Nothing is removed before the confirmation, the plan shown is the plan
/// performed, and every effect lands in the journal that lives outside the tree being removed.
/// </summary>
public sealed class UninstallWizardViewModel : ObservableObject
{
    private static readonly IReadOnlyList<(UninstallPage Page, string Title)> Steps =
    [
        (UninstallPage.Choose, "What to remove"),
        (UninstallPage.Confirm, "Confirm"),
        (UninstallPage.Progress, "Remove"),
        (UninstallPage.Finish, "Finished"),
    ];

    private readonly InstallationLayout layout;
    private readonly string homeDirectory;
    private readonly IProcessRunner processRunner;
    private readonly UninstallPlanner planner;

    private UninstallPage currentPage = UninstallPage.Choose;
    private RemovalPreset selectedPreset = RemovalPreset.FullUninstall;
    private bool removeSigningKeys;
    private bool isConfirmed;
    private bool isRunning;
    private bool isComplete;
    private bool hasRunError;
    private bool hasLoaded;
    private string? blockingNotice;
    private string? unexpectedError;
    private string previewText = string.Empty;
    private string summary = string.Empty;
    private string report = string.Empty;
    private int progress;
    private string progressText = "Ready";
    private UninstallPlan? confirmedPlan;
    private CancellationTokenSource? runCancellation;

    /// <summary>
    /// The parameters exist so tests can point the wizard at a machine of their own making;
    /// left alone it works on this computer. <paramref name="readHooksPath"/> is how a
    /// repository's <c>core.hooksPath</c> is discovered — Git, unless a test says otherwise.
    /// </summary>
    public UninstallWizardViewModel(
        InstallationLayout? layout = null,
        string? homeDirectory = null,
        IProcessRunner? processRunner = null,
        Func<string, CancellationToken, Task<string?>>? readHooksPath = null)
    {
        this.layout = layout ?? InstallationLayout.CreateDefault();
        this.homeDirectory = homeDirectory ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.processRunner = processRunner ?? new SystemProcessRunner();
        planner = new UninstallPlanner(this.layout, this.homeDirectory, readHooksPath);

        foreach (var item in RemovalMatrix.Items.Where(item => item != RemovalItem.SigningKeys))
        {
            Rows.Add(new RemovalRow(item));
        }

        ApplyPreset(selectedPreset);
        BackCommand = new RelayCommand(() => MovePrevious(), () => CanMovePrevious);
        NextCommand = new AsyncRelayCommand(
            () => MoveNextAsync(),
            () => CanMoveNext,
            ReportUnexpectedError);
        UninstallCommand = new AsyncRelayCommand(
            () => RunAsync(),
            () => CanRun,
            ReportUnexpectedError);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
    }

    public event EventHandler? CloseRequested;

    /// <summary>
    /// Where the journal is written: outside the runtime root, so the record of what this run
    /// removed survives the removal. Settable so tests point it at their own directory.
    /// </summary>
    public string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        RemovalMatrix.JournalDirectoryName);

    public ObservableCollection<RemovalRow> Rows { get; } = [];

    public ObservableCollection<RepositoryRow> Repositories { get; } = [];

    public ObservableCollection<WizardStep> StepList { get; } = [];

    public IReadOnlyList<UninstallPresetOption> Presets { get; } = RemovalMatrix.Presets
        .Select(preset => new UninstallPresetOption(preset))
        .ToArray();

    public RelayCommand BackCommand { get; }

    public AsyncRelayCommand NextCommand { get; }

    public AsyncRelayCommand UninstallCommand { get; }

    public RelayCommand CancelCommand { get; }

    public UninstallPage CurrentPage
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

    public RemovalPreset SelectedPreset
    {
        get => selectedPreset;
        set
        {
            if (selectedPreset == value)
            {
                return;
            }

            selectedPreset = value;
            ApplyPreset(value);
            OnPropertyChanged();
            RefreshAll();
        }
    }

    /// <summary>
    /// The separate confirmation the key directory needs. Kept off the matrix rows on purpose:
    /// with it ticked the offline backup becomes the only copy that exists anywhere.
    /// </summary>
    public bool RemoveSigningKeys
    {
        get => removeSigningKeys;
        set
        {
            SetProperty(ref removeSigningKeys, value);
            RefreshAll();
        }
    }

    public string SigningKeysTitle => RemovalMatrix.Title(RemovalItem.SigningKeys);

    public string SigningKeysWarning =>
        Path.Combine(layout.Root, RemovalMatrix.SigningKeyDirectoryName) +
        " holds the private half of the release signing pair. Remove it only if you have the " +
        "offline backup: it becomes the only copy that exists.";

    public bool IsConfirmed
    {
        get => isConfirmed;
        set
        {
            SetProperty(ref isConfirmed, value);
            RefreshAll();
        }
    }

    /// <summary>
    /// Why this wizard will not do anything: another run holds the live lock, or there is no
    /// installation here to remove. Both are answers, not errors.
    /// </summary>
    public string? BlockingNotice => blockingNotice;

    public bool IsBlocked => blockingNotice is not null;

    public string? UnexpectedError => unexpectedError;

    public bool HasUnexpectedError => unexpectedError is not null;

    public string PreviewText => previewText;

    public string Summary => summary;

    public string Report => report;

    public int Progress => progress;

    public string ProgressText => progressText;

    public bool IsRunning => isRunning;

    public bool IsComplete => isComplete;

    public bool HasRunError => hasRunError;

    public bool IsProgressPage => CurrentPage == UninstallPage.Progress;

    public bool IsFinishPage => CurrentPage == UninstallPage.Finish;

    public string StepStatus => $"Step {StepIndex(CurrentPage) + 1} of {Steps.Count}";

    public string StepTitle => CurrentPage switch
    {
        UninstallPage.Choose => "What to remove",
        UninstallPage.Confirm => "Ready to remove",
        UninstallPage.Progress => "Removing",
        _ => hasRunError ? "Removal not completed" : "Removal complete",
    };

    public string StepDescription => CurrentPage switch
    {
        UninstallPage.Choose =>
            "Start from one of the three presets and change any row you like. Nothing is " +
            "removed until you confirm it on the next page.",
        UninstallPage.Confirm =>
            "Everything this will remove, and everything it will leave alone. To change " +
            "anything click Back; to apply it click Uninstall.",
        UninstallPage.Progress => "Applying the selected removals.",
        _ => hasRunError
            ? "Some things could not be removed. The report below says which, and why."
            : "Everything selected was removed.",
    };

    public bool CanMovePrevious =>
        CurrentPage == UninstallPage.Confirm && !isRunning;

    public bool CanMoveNext =>
        !isRunning && !IsBlocked && CurrentPage == UninstallPage.Choose;

    public bool CanRun =>
        CurrentPage == UninstallPage.Confirm &&
        !isRunning &&
        isConfirmed &&
        confirmedPlan is { HasWork: true };

    public bool CanCancel => !isRunning || !IsFinishPage;

    public bool IsNextVisible => CurrentPage == UninstallPage.Choose;

    public bool IsUninstallVisible => CurrentPage == UninstallPage.Confirm;

    public string CancelButtonText => IsFinishPage ? "Close" : "Cancel";

    public int CurrentPageIndex => StepIndex(CurrentPage);

    /// <summary>
    /// Reads the machine once: whether another run is happening, whether there is anything
    /// installed at all, and which repositories carry the dispatchers.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (hasLoaded)
        {
            return;
        }

        hasLoaded = true;
        if (InstallerRunJournal.IsRunActive(LogDirectory))
        {
            // The two must not meet in the middle: one wizard writing the runtime while
            // another removes it leaves a tree neither of them describes.
            blockingNotice =
                "Another LocalAi installer or uninstaller is running on this computer. " +
                "Finish it first, then start this one again.";
            RefreshAll();
            return;
        }

        if (!Directory.Exists(layout.Root))
        {
            blockingNotice =
                "There is no LocalAi installation at " + layout.Root + " to remove. " +
                "Client registrations and Git hooks, if any are left, are listed by the " +
                "documentation.";
            RefreshAll();
            return;
        }

        // Planned as a full uninstall purely to inventory the connected repositories: the
        // page has to list them before anybody has chosen anything.
        var inventory = await planner.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            cancellationToken);
        Repositories.Clear();
        foreach (var hook in inventory.Hooks)
        {
            Repositories.Add(new RepositoryRow(
                hook.RepositoryId,
                hook.CommonDirectory,
                hook.Dispatchers.Count,
                hook.SkipReason));
        }

        RefreshAll();
    }

    public async Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        if (!CanMoveNext)
        {
            return false;
        }

        // The preview is built from the current choices at the moment the person asks to see
        // it, and the plan they then confirm is the object apply is handed — so what the page
        // listed is what runs, not a second planning pass that could differ.
        confirmedPlan = await planner.PlanAsync(Selection(), cancellationToken);
        previewText = confirmedPlan.PreviewText;
        OnPropertyChanged(nameof(PreviewText));
        CurrentPage = UninstallPage.Confirm;
        RefreshAll();
        return true;
    }

    public bool MovePrevious()
    {
        if (!CanMovePrevious)
        {
            return false;
        }

        // Going back invalidates the plan: the next Next builds a fresh one from whatever the
        // page says then.
        confirmedPlan = null;
        isConfirmed = false;
        OnPropertyChanged(nameof(IsConfirmed));
        CurrentPage = UninstallPage.Choose;
        return true;
    }

    public void Cancel()
    {
        if (isRunning)
        {
            runCancellation?.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRun || confirmedPlan is not { } plan)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCancellation = linked;
        isRunning = true;
        isComplete = false;
        hasRunError = false;
        CurrentPage = UninstallPage.Progress;
        SetProgress(5, "Asking LocalAi to stop...");

        var log = new StringBuilder();
        InstallerRunJournal? journal = null;
        try
        {
            journal = InstallerRunJournal.Start(LogDirectory);
            var outcome = await new UninstallRunner(layout, processRunner)
                .ApplyAsync(plan, journal, linked.Token);
            hasRunError = !outcome.Succeeded;
            isComplete = outcome.Succeeded;
            journal.Finish(outcome.Succeeded
                ? InstallerRunOutcome.Completed
                : InstallerRunOutcome.Failed);
            summary = BuildSummary(outcome);
            report = BuildReport(plan, outcome, log);
            SetProgress(100, outcome.Succeeded ? "Completed" : "Finished with problems");
            CurrentPage = UninstallPage.Finish;
            return outcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            journal?.Finish(InstallerRunOutcome.Cancelled);
            hasRunError = true;
            summary =
                "Cancelled. Whatever had already been removed stays removed — the report " +
                "below and the journal say what that was.";
            report = log.ToString();
            SetProgress(progress, "Cancelled");
            CurrentPage = UninstallPage.Finish;
            return false;
        }
        catch (UninstallRefusedException refusal)
        {
            // The run stopped before touching anything, which is the one failure worth
            // stating as its own outcome: the machine is exactly as it was.
            journal?.Finish(InstallerRunOutcome.Failed);
            hasRunError = true;
            summary = refusal.Message;
            report = "Nothing was removed.";
            SetProgress(100, "Refused");
            CurrentPage = UninstallPage.Finish;
            return false;
        }
        catch (Exception exception)
        {
            journal?.Finish(InstallerRunOutcome.Failed);
            hasRunError = true;
            summary = "Removal failed: " + exception.Message;
            report = log.ToString();
            SetProgress(100, "Failed");
            CurrentPage = UninstallPage.Finish;
            return false;
        }
        finally
        {
            journal?.Dispose();
            isRunning = false;
            runCancellation = null;
            RefreshAll();
        }
    }

    public void ReportUnexpectedError(Exception exception)
    {
        unexpectedError = $"Unexpected error: {exception.GetType().Name}: {exception.Message}";
        OnPropertyChanged(nameof(UnexpectedError));
        OnPropertyChanged(nameof(HasUnexpectedError));
    }

    /// <summary>The choices as the core understands them.</summary>
    public RemovalSelection Selection()
    {
        var selection = RemovalSelection.FromPreset(selectedPreset);
        foreach (var row in Rows)
        {
            selection = selection.With(row.Item, row.IsSelected);
        }

        selection = selection.WithSigningKeyRemoval(removeSigningKeys);
        return Repositories.Count == 0
            ? selection
            : selection.WithRepositories(Repositories
                .Where(repository => repository.IsSelected)
                .Select(repository => repository.RepositoryId));
    }

    private void ApplyPreset(RemovalPreset preset)
    {
        var selection = RemovalSelection.FromPreset(preset);
        var undecided = selection.ItemsNeedingDecision.ToHashSet();
        foreach (var row in Rows)
        {
            row.IsSelected = selection.Includes(row.Item);
            row.NeedsDecision = undecided.Contains(row.Item);
        }

        // The keys are never prefilled by a preset. Their checkbox is the confirmation.
        removeSigningKeys = false;
        OnPropertyChanged(nameof(RemoveSigningKeys));
    }

    private string BuildSummary(UninstallOutcome outcome)
    {
        if (!outcome.Succeeded)
        {
            return outcome.Failures.Count + " thing(s) could not be removed; " +
                outcome.RemovedPaths.Count + " were. Nothing else was touched.";
        }

        var summary = new StringBuilder();
        summary.Append(outcome.RuntimeRootRemoved
            ? "LocalAi was removed from this computer."
            : outcome.RemovedPaths.Count + " path(s) removed from " + layout.Root + ".");
        if (outcome.RewrittenConfigurations.Count > 0)
        {
            summary.Append(" Client configurations updated: ")
                .Append(outcome.RewrittenConfigurations.Count)
                .Append('.');
        }

        if (outcome.RemovedHooks.Count > 0)
        {
            summary.Append(' ')
                .Append(outcome.RemovedHooks.Count)
                .Append(" Git hook dispatcher(s) removed.");
        }

        return summary.ToString();
    }

    private string BuildReport(
        UninstallPlan plan,
        UninstallOutcome outcome,
        StringBuilder log)
    {
        foreach (var path in outcome.RemovedPaths)
        {
            log.AppendLine("Removed " + path);
        }

        foreach (var path in outcome.RewrittenConfigurations)
        {
            log.AppendLine("Updated " + path);
        }

        foreach (var hook in outcome.RemovedHooks)
        {
            log.AppendLine("Removed hook " + hook);
        }

        foreach (var failure in outcome.Failures)
        {
            log.AppendLine("Could not remove " + failure.Path + ": " + failure.Reason);
        }

        // Repeated on the last page rather than only on the review page: the prerequisites and
        // the models are the part a person goes looking for afterwards.
        foreach (var notice in plan.Retained)
        {
            log.AppendLine("Kept: " + notice.Title + " — " + notice.Detail);
        }

        return log.ToString().Trim();
    }

    private void SetProgress(int value, string text)
    {
        progress = value;
        progressText = text;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    private static int StepIndex(UninstallPage page)
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

    private void RebuildSteps()
    {
        StepList.Clear();
        var current = StepIndex(CurrentPage);
        for (var index = 0; index < Steps.Count; index++)
        {
            StepList.Add(new WizardStep(
                Steps[index].Title,
                index == current,
                index < current));
        }
    }

    private void RefreshAll()
    {
        RebuildSteps();
        foreach (var name in new[]
                 {
                     nameof(CurrentPage), nameof(CurrentPageIndex), nameof(StepTitle),
                     nameof(StepDescription), nameof(StepStatus), nameof(IsProgressPage),
                     nameof(IsFinishPage), nameof(IsRunning), nameof(IsComplete),
                     nameof(HasRunError), nameof(CanMoveNext), nameof(CanMovePrevious),
                     nameof(CanRun), nameof(CanCancel), nameof(IsNextVisible),
                     nameof(IsUninstallVisible), nameof(CancelButtonText), nameof(Summary),
                     nameof(Report), nameof(BlockingNotice), nameof(IsBlocked),
                 })
        {
            OnPropertyChanged(name);
        }

        BackCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
