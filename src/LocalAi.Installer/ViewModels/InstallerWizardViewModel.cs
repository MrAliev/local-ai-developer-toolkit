using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.ViewModels;

public sealed class InstallerWizardViewModel : ObservableObject
{
    private static readonly TimeSpan DependencyInstallTimeout = TimeSpan.FromMinutes(10);

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
    private readonly PackagePageViewModel package = new();
    private readonly ModelsPageViewModel models = new();
    private readonly ResidencyPageViewModel residency = new();
    private readonly AgentIntegrationPageViewModel agents = new();
    private readonly ReviewApplyPageViewModel review = new();
    private readonly FinishPageViewModel finish = new();
    private readonly WindowsEnvironmentDetector environmentDetector;
    private readonly IProcessRunner processRunner;

    private CancellationTokenSource? runCancellation;
    private InstallerPage currentPage = InstallerPage.Diagnose;
    private bool isRunning;
    private bool isComplete;
    private bool hasRunError;
    private bool wasCancelled;
    private bool hasInitialized;
    private int progress;
    private string progressText = "Ready";
    private string? rollbackMessage;
    private EnvironmentDiagnosis? environmentDiagnosis;
    private CatalogRecommendation lastRecommendation = CatalogRecommendation.Empty;

    public InstallerWizardViewModel()
    {
        var runner = new SystemProcessRunner();
        processRunner = runner;
        environmentDetector = new WindowsEnvironmentDetector(
            new SystemEnvironmentProbe(),
            new SystemFileSystemProbe(),
            runner,
            new WindowsInstalledApplicationProbe(),
            new SystemDiskProbe(),
            new SystemNetworkProbe(),
            new WindowsGpuProbe(new DxgiNativeGpuAdapterEnumerator()));

        BackCommand = new RelayCommand(() => MovePrevious(), () => CanMovePrevious);
        NextCommand = new RelayCommand(() => MoveNext(), () => CanMoveNext);
        InstallCommand = new AsyncRelayCommand(() => RunAsync(), () => CanRun);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);

        // Relaxing the residency policy immediately widens what the models page can offer,
        // so the two pages stay consistent instead of contradicting each other.
        residency.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ResidencyPageViewModel.Policy))
            {
                OnResidencyChanged();
            }
        };

        RebuildSteps();
    }

    public event EventHandler? CloseRequested;

    public bool EnableDependencyActions { get; set; }

    public RelayCommand BackCommand { get; }

    public RelayCommand NextCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    public RelayCommand CancelCommand { get; }

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

    public string? RollbackResult => rollbackMessage;

    public string? FinishSummary => finish.Summary;

    public int CurrentPageIndex => StepIndex(CurrentPage);

    /// <summary>
    /// Back and Next stay on screen for the whole wizard and only change availability.
    /// Buttons that vanish make the panel jump and hide where the user is.
    /// </summary>
    public bool CanMovePrevious =>
        CurrentPage is not (InstallerPage.Diagnose or InstallerPage.Progress) &&
        !isRunning &&
        !IsFinishPage;

    public bool CanMoveNext => CurrentPage switch
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

        await RefreshEnvironmentDiagnosticsAsync(cancellationToken);
        hasInitialized = true;
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
            // Applied first so an interrupted run still leaves the machine with the
            // residency setting the user actually chose.
            ApplyResidencyPolicy(report);

            if (!EnableDependencyActions)
            {
                finish.Progress = report.ToString().Trim();
                finish.Summary = "Dry run completed. Nothing was installed.";
                SetProgress(100, "Completed");
                isComplete = true;
                CurrentPage = InstallerPage.Finish;
                return true;
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
                var installed = await TryInstallDependencyWithWingetAsync(
                    definition,
                    dependency.Title,
                    reinstall: dependency.IsInstalled,
                    token);
                AppendLog(report, installed
                    ? $"{dependency.Title}: done."
                    : $"{dependency.Title}: failed.");
                if (installed)
                {
                    successfulActions++;
                }
                else
                {
                    failedActions++;
                    hasRunError = true;
                }

                await RefreshEnvironmentDiagnosticsAsync(token);
            }

            // The package goes last: prerequisites must be in place first, and a failure
            // here must not leave half-installed dependencies unexplained.
            await InstallPackageAsync(report, token);

            SetProgress(95, "Finalising...");
            AppendLog(report, "Finalising.");
            finish.Summary = BuildFinishSummary(
                requested,
                successfulActions,
                skippedActions,
                failedActions);
            SetProgress(100, hasRunError ? "Failed" : "Completed");
            isComplete = !hasRunError;
            finish.Progress = report.ToString().Trim();
            SetRollbackInfo(report.ToString(), false);
            CurrentPage = InstallerPage.Finish;
            return !hasRunError;
        }
        catch (OperationCanceledException)
        {
            AppendLog(report, "Cancelled. Actions already applied were left in place.");
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
            SetRollbackInfo(report.ToString(), false);
            CurrentPage = InstallerPage.Finish;
            return false;
        }
        finally
        {
            isRunning = false;
            runCancellation = null;
            RefreshAll();
        }
    }

    public void SetProgress(int value, string message)
    {
        progress = Math.Clamp(value, 0, 100);
        progressText = message;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    public void SetRollbackInfo(string message, bool requiresRestart)
    {
        rollbackMessage = message;
        finish.RequiresRestart = requiresRestart;
        finish.RollbackNotes = message;
        OnPropertyChanged(nameof(RollbackResult));
    }

    public void RefreshNavigationState() => RefreshAll();

    private void ApplyResidencyPolicy(StringBuilder report)
    {
        try
        {
            var store = new ModelResidencyPolicyStore(
                ModelResidencyPolicyStore.DefaultRuntimeRoot);
            store.Write(store.Read() with { ModelResidency = residency.Policy });
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
            AppendLog(report, $"Could not store the residency policy: {exception.Message}");
            hasRunError = true;
        }
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
        BackCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private static void AppendLog(StringBuilder report, string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            report.AppendLine(message);
        }
    }

    private static string BuildFinishSummary(
        int requestedActions,
        int successfulActions,
        int skippedActions,
        int failedActions,
        string? fatalMessage = null)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"Requested: {requestedActions}.");
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
        var diagnosis = await environmentDetector.DetectAsync(cancellationToken);
        environmentDiagnosis = diagnosis;

        diagnose.Load(diagnosis);
        residency.HasUsableAdapter = diagnose.HasUsableAdapter;
        agents.ApplyDetection(diagnosis.Agents);
        await RefreshRecommendationAsync(diagnosis, cancellationToken);

        // Detection records presence only; it must not grant consent on the user's behalf.
        dependencies.SetInstalled("Git", diagnosis.Git.State == DependencyState.Detected);
        dependencies.SetInstalled("Ollama", diagnosis.Ollama.State == DependencyState.Detected);
        dependencies.SetInstalled("GitHubCli", await IsGitHubCliPresentAsync(cancellationToken));

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
    /// The environment detector does not probe the GitHub CLI, so it is checked here. Only
    /// presence is established: whether this machine is signed in surfaces when a release is
    /// actually resolved, with the CLI's own message.
    /// </summary>
    private async Task<bool> IsGitHubCliPresentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAsync(
                "gh",
                ["--version"],
                TimeSpan.FromSeconds(15),
                cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static DependencyDefinition? ResolveDependencyDefinition(string dependencyId) =>
        dependencyId switch
        {
            "Git" => DependencyCatalog.Git,
            "Ollama" => DependencyCatalog.Ollama,
            "GitHubCli" => DependencyCatalog.GitHubCli,
            _ => null,
        };

    /// <summary>
    /// Scratch space for downloaded release assets, under the installer's own directory so a
    /// failed run leaves nothing behind in the installed layout.
    /// </summary>
    private static string WorkingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi",
        "installer",
        "downloads");

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
            var feed = new GitHubReleaseFeed(processRunner);
            package.SelectResolvedRelease(
                await feed.ResolveAsync(
                    package.ReleaseVersion.Trim(),
                    WorkingDirectory,
                    cancellationToken));
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

    private async Task InstallPackageAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        if (package.Resolved is not { } resolved)
        {
            AppendLog(report, "LocalAi package: no verified release selected, skipping.");
            return;
        }

        SetProgress(Math.Max(progress, 40), "Downloading the LocalAi package...");
        AppendLog(
            report,
            $"LocalAi package {resolved.Manifest.ReleaseVersion}: downloading and verifying...");

        var service = new ReleaseInstallService(
            new GitHubReleaseFeed(processRunner),
            processRunner,
            new SystemFileSystemProbe());
        var result = await service.InstallAsync(resolved, WorkingDirectory, cancellationToken);

        AppendLog(
            report,
            result.Installed
                ? $"LocalAi package: {result.Status}, version {result.Version} at {result.VersionPath}."
                : $"LocalAi package: {result.Status}. {result.Reason}".Trim());
        if (!result.Installed)
        {
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
            SetRollbackInfo(
                $"WinGet is unavailable; install {displayName} manually from " +
                $"{dependency.OfficialInstallerUri}.",
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
            wingetPath,
            [.. arguments],
            DependencyInstallTimeout,
            cancellationToken);
        if (result.ExitCode is not 0)
        {
            SetRollbackInfo(
                $"{displayName} installation failed with exit code {result.ExitCode}.",
                false);
            return false;
        }

        return true;
    }
}
