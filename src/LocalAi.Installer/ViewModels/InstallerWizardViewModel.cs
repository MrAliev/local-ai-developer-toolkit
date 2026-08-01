using System.Text;
using System.Windows;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.ViewModels;

public sealed class InstallerWizardViewModel : ObservableObject
{
    private readonly DiagnosePageViewModel diagnose = new();
    private readonly DependenciesPageViewModel dependencies = new();
    private readonly PackagePageViewModel package = new();
    private readonly ModelsPageViewModel models = new();
    private readonly AgentIntegrationPageViewModel agents = new();
    private readonly ReviewApplyPageViewModel review = new();
    private readonly FinishPageViewModel finish = new();
    private readonly WindowsEnvironmentDetector environmentDetector;
    private readonly IProcessRunner processRunner;

    private InstallerPage currentPage = InstallerPage.Diagnose;
    private bool isCanceled;
    private bool isComplete;
    private bool isRunning;
    private int progress;
    private string progressText = "Ready";
    private bool hasRunError;
    private string? rollbackMessage;
    private string language = InstallerCulture.CurrentCultureCode;
    private bool hasInitialized;
    private EnvironmentDiagnosis? environmentDiagnosis;

    private static readonly TimeSpan DependencyInstallTimeout = TimeSpan.FromMinutes(10);

    public InstallerWizardViewModel()
    {
        var processRunner = new SystemProcessRunner();
        this.processRunner = processRunner;
        environmentDetector = new WindowsEnvironmentDetector(
            new SystemEnvironmentProbe(),
            new SystemFileSystemProbe(),
            processRunner,
            new WindowsInstalledApplicationProbe(),
            new SystemDiskProbe(),
            new SystemNetworkProbe(),
            new WindowsGpuProbe(new DxgiNativeGpuAdapterEnumerator()));

        package.SelectCompatibleRelease("latest", true);

        for (var i = 0; i < agents.Agents.Count; i++)
        {
            var agent = agents.Agents[i];
            agents.Agents[i] = agent with { Choice = AgentChoice.RunWithoutAgent };
        }

        foreach (var dependency in dependencies.Dependencies)
        {
            dependency.IsConsented = true;
        }

        OnPropertyChanged(nameof(Dependencies));

        review.IsConfirmed = true;
    }

    public bool EnableDependencyActions { get; set; }

    public IReadOnlyList<string> PageNames { get; } =
    [
        "Diagnosis",
        "Dependencies",
        "Package",
        "Models",
        "Agents",
        "Review",
        "Apply",
    ];

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
            OnPropertyChanged();
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(StepDescription));
            OnPropertyChanged(nameof(StepStatus));
            OnPropertyChanged(nameof(CanMovePrevious));
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(RunButtonVisibility));
            OnPropertyChanged(nameof(NextButtonVisibility));
            OnPropertyChanged(nameof(BackButtonVisibility));
            OnPropertyChanged(nameof(CloseButtonVisibility));
            OnPropertyChanged(nameof(IsFinishPage));
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
        summary.AppendLine("Execution summary:");
        summary.AppendLine($"Requested dependency actions: {requestedActions}.");
        summary.AppendLine($"Installed/reinstalled: {successfulActions}.");
        summary.AppendLine($"Skipped: {skippedActions}.");
        summary.AppendLine($"Failed: {failedActions}.");
        if (!string.IsNullOrWhiteSpace(fatalMessage))
        {
            summary.AppendLine($"Fatal error: {fatalMessage}");
        }

        return summary.ToString().Trim();
    }

    public DiagnosePageViewModel Diagnose => diagnose;
    public DependenciesPageViewModel Dependencies => dependencies;
    public PackagePageViewModel Package => package;
    public ModelsPageViewModel Models => models;
    public AgentIntegrationPageViewModel Agents => agents;
    public ReviewApplyPageViewModel Review => review;
    public FinishPageViewModel Finish => finish;

    public string StepTitle => PageNames[(int)CurrentPage];

    public string StepDescription => CurrentPage switch
    {
        InstallerPage.Diagnose => "Run environment and compatibility checks.",
        InstallerPage.Dependencies => "Select optional dependency installation and consent.",
        InstallerPage.Package => "Choose a target package and confirm compatibility.",
        InstallerPage.Models => "Pick model strategy: automatic or manual.",
        InstallerPage.Agents => "Choose per-agent integration options.",
        InstallerPage.ReviewApply => "Review planned actions before applying.",
        _ => "Installation finished.",
    };

    public string StepStatus => $"Step {(int)CurrentPage + 1} of {PageNames.Count}";

    public bool CanMovePrevious => CurrentPage > InstallerPage.Diagnose && !isCanceled && !isRunning;

    public bool CanMoveNext => CurrentPage switch
    {
        InstallerPage.Diagnose => !isCanceled && diagnose.CanContinue,
        InstallerPage.Dependencies => !isCanceled && dependencies.CanContinue,
        InstallerPage.Package => !isCanceled && package.CanContinue,
        InstallerPage.Models => !isCanceled && models.CanContinue,
        InstallerPage.Agents => !isCanceled && agents.CanContinue,
        InstallerPage.ReviewApply => !isCanceled && !isRunning && review.CanApply,
        _ => false,
    };

    public bool CanRun =>
        CurrentPage == InstallerPage.ReviewApply && !isCanceled && !isRunning && review.CanApply;

    public Visibility RunButtonVisibility =>
        CanRun ? Visibility.Visible : Visibility.Collapsed;

    public bool IsCanceled => isCanceled;

    public bool IsComplete => isComplete;

    public bool HasRunError => hasRunError;

    public bool IsFinishPage => CurrentPage == InstallerPage.Finish;

    public int Progress => progress;

    public string ProgressText => progressText;

    public string? ReviewText => review.Render(diagnose, dependencies, models, agents);

    public string Language
    {
        get => language;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                value = "en-US";
            }

            SetProperty(ref language, value);
            InstallerCulture.CurrentCultureCode = value;
            OnPropertyChanged(nameof(IsRussian));
        }
    }

    public bool IsRussian => string.Equals(Language, "ru-RU", StringComparison.Ordinal);

    public bool IsRunning => isRunning;

    public bool RequiresRestart => finish.RequiresRestart;

    public string? RollbackResult => rollbackMessage;

    public int CurrentPageIndex => (int)CurrentPage;

    public string? FinishSummary => finish.Summary;

    public Visibility BackButtonVisibility =>
        (IsFinishPage && !hasRunError) || !CanMovePrevious
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility NextButtonVisibility =>
        CurrentPage == InstallerPage.Finish
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility CloseButtonVisibility =>
        IsFinishPage
            ? Visibility.Visible
            : Visibility.Collapsed;

    public void RefreshNavigationState()
    {
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunButtonVisibility));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(ReviewText));
        OnPropertyChanged(nameof(HasRunError));
        OnPropertyChanged(nameof(IsFinishPage));
        OnPropertyChanged(nameof(BackButtonVisibility));
        OnPropertyChanged(nameof(NextButtonVisibility));
        OnPropertyChanged(nameof(CloseButtonVisibility));
        OnPropertyChanged(nameof(FinishSummary));
    }

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
        RefreshNavigationState();
    }

    public bool MoveNext()
    {
        if (!CanMoveNext)
        {
            return false;
        }

        if (CurrentPage is InstallerPage.ReviewApply)
        {
            return false;
        }

        CurrentPage = CurrentPage switch
        {
            InstallerPage.Diagnose => InstallerPage.Dependencies,
            InstallerPage.Dependencies => InstallerPage.Package,
            InstallerPage.Package => InstallerPage.Models,
            InstallerPage.Models => InstallerPage.Agents,
            InstallerPage.Agents => InstallerPage.ReviewApply,
            InstallerPage.Finish => InstallerPage.Finish,
            _ => CurrentPage,
        };
        return true;
    }

    public bool MovePrevious()
    {
        if (!CanMovePrevious || isCanceled)
        {
            return false;
        }

        CurrentPage = CurrentPage switch
        {
            InstallerPage.Dependencies => InstallerPage.Diagnose,
            InstallerPage.Package => InstallerPage.Dependencies,
            InstallerPage.Models => InstallerPage.Package,
            InstallerPage.Agents => InstallerPage.Models,
            InstallerPage.ReviewApply => InstallerPage.Agents,
            InstallerPage.Finish => InstallerPage.ReviewApply,
            _ => InstallerPage.Diagnose,
        };
        return true;
    }

    public bool Run()
    {
        return RunAsync().GetAwaiter().GetResult();
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRun || isCanceled)
        {
            return false;
        }

        if (!EnableDependencyActions)
        {
            finish.Progress = "Dry-run mode: execution step simulation only.";
            finish.Summary = "Execution was started in dry-run mode. No changes were applied.";
            CurrentPage = InstallerPage.Finish;
            progress = 100;
            progressText = "Completed";
            isComplete = true;
            hasRunError = false;
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(RequiresRestart));
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(RunButtonVisibility));
            OnPropertyChanged(nameof(FinishSummary));
            OnPropertyChanged(nameof(CloseButtonVisibility));
            OnPropertyChanged(nameof(BackButtonVisibility));
            return true;
        }

        isRunning = true;
        isComplete = false;
        hasRunError = false;
        RefreshNavigationState();
        var report = new StringBuilder();
        var totalDependencies = dependencies.Dependencies.Count(dependency => CanInstall(dependency.Id));
        var actionsToRun = dependencies.Dependencies.Count(dependency => CanInstall(dependency.Id) && dependency.IsConsented);
        var successfulActions = 0;
        var failedActions = 0;
        var skippedActions = 0;
        var requestedActions = Math.Max(actionsToRun, 0);
        AppendExecutionLog(report, "Running selected dependency actions.");
        if (totalDependencies == 0)
        {
            AppendExecutionLog(report, "No dependency actions were selected.");
        }
        else
        {
            AppendExecutionLog(report, $"Dependencies considered: {totalDependencies}, selected: {actionsToRun}.");
        }

        SetProgress(5, "Preparing...");
        int nextProgress = 10;
        try
        {
            await RefreshEnvironmentDiagnosticsAsync(cancellationToken);

            foreach (var dependency in dependencies.Dependencies)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    AppendExecutionLog(report, "Installation cancelled.");
                    SetProgress(nextProgress, "Cancelled");
                    break;
                }

                if (!CanInstall(dependency.Id))
                {
                    continue;
                }

                if (!dependency.IsConsented)
                {
                    AppendExecutionLog(report, $"{dependency.Title}: skipped.");
                    skippedActions++;
                    continue;
                }

                var definition = ResolveDependencyDefinition(dependency.Id);
                if (definition is null)
                {
                    AppendExecutionLog(report, $"{dependency.Title}: no automatic installer recipe is available.");
                    failedActions++;
                    hasRunError = true;
                    continue;
                }

                SetProgress(nextProgress, $"Installing {dependency.Title}...");
                AppendExecutionLog(report, $"Installing {dependency.Title}...");
                nextProgress = Math.Clamp(nextProgress + 25, 0, 95);
                var installed = await TryInstallDependencyWithWingetAsync(
                        definition,
                        dependency.Title,
                        cancellationToken);
                AppendExecutionLog(
                    report,
                    installed
                        ? $"{dependency.Title}: {(dependency.IsInstalled ? "reinstalled." : "installed.")}"
                        : $"{dependency.Title}: install attempt failed.");
                if (installed)
                {
                    successfulActions++;
                }
                else
                {
                    failedActions++;
                    hasRunError = true;
                }

                await RefreshEnvironmentDiagnosticsAsync(cancellationToken);
            }

            SetProgress(95, "Finalizing...");
            finish.Summary = BuildFinishSummary(
                requestedActions,
                successfulActions,
                skippedActions,
                failedActions);
            AppendExecutionLog(report, "Finalizing changes.");
            progress = 100;
            isComplete = !hasRunError && !cancellationToken.IsCancellationRequested;
            progressText = isComplete ? "Completed" : "Failed";
            if (report.Length == 0)
            {
                AppendExecutionLog(report, "No dependency actions selected.");
            }

            finish.Progress = report.ToString().Trim();
            if (!isComplete)
            {
                hasRunError = true;
            }

            SetRollbackInfo(report.ToString(), false);
            CurrentPage = InstallerPage.Finish;
            return true;
        }
        catch (Exception exception)
        {
            AppendExecutionLog(report, $"Run failed: {exception.Message}");
            progressText = "Failed";
            progress = 100;
            isComplete = false;
            hasRunError = true;
            finish.Summary = BuildFinishSummary(
                requestedActions,
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
            OnPropertyChanged(nameof(IsRunning));
            RefreshNavigationState();
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(RequiresRestart));
        }
    }

    private void AppendExecutionLog(StringBuilder report, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        report.AppendLine(message);
        finish.Progress = report.ToString().Trim();
    }

    public void Cancel()
    {
        isCanceled = true;
        isComplete = false;
        OnPropertyChanged(nameof(IsCanceled));
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(CanRun));
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
        OnPropertyChanged(nameof(RequiresRestart));
    }

    public void ConfirmReview()
    {
        review.IsConfirmed = true;
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunButtonVisibility));
    }

    private async Task RefreshEnvironmentDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var diagnosis = await environmentDetector
            .DetectAsync(cancellationToken);
        environmentDiagnosis = diagnosis;

        diagnose.SetResult(
            diagnosis.IsSupported,
            diagnosis.UnsupportedReasons.Count == 0
                ? null
                : string.Join("; ", diagnosis.UnsupportedReasons));

        dependencies.SetInstalled("Git", diagnosis.Git.State == DependencyState.Detected);
        dependencies.SetInstalled("Ollama", diagnosis.Ollama.State == DependencyState.Detected);

        if (diagnosis.Git.State == DependencyState.Detected)
        {
            dependencies.SetConsent("Git", true);
        }

        if (diagnosis.Ollama.State == DependencyState.Detected)
        {
            dependencies.SetConsent("Ollama", true);
        }

        if (diagnosis.UnsupportedReasons.Count > 0)
        {
            finish.Progress = string.Join(
                Environment.NewLine,
                diagnosis.UnsupportedReasons);
        }

        OnPropertyChanged(nameof(Diagnose));
        OnPropertyChanged(nameof(Dependencies));
        OnPropertyChanged(nameof(ProgressText));
        RefreshNavigationState();
    }

    private static bool CanInstall(string dependencyId) =>
        dependencyId switch
        {
            "Git" => true,
            "Ollama" => true,
            _ => false,
        };

    private static DependencyDefinition? ResolveDependencyDefinition(string dependencyId) =>
        dependencyId switch
        {
            "Git" => DependencyCatalog.Git,
            "Ollama" => DependencyCatalog.Ollama,
            _ => null,
        };

    private async Task<bool> TryInstallDependencyWithWingetAsync(
        DependencyDefinition dependency,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (environmentDiagnosis is null)
        {
            return false;
        }

        if (environmentDiagnosis.WinGet.State != DependencyState.Detected ||
            environmentDiagnosis.WinGet.ExecutablePath is null)
        {
            var message =
                $"WinGet is not available; install {displayName} manually from {dependency.OfficialInstallerUri}.";
            SetRollbackInfo(message, false);
            finish.Progress = message + Environment.NewLine + (finish.Progress ?? string.Empty);
            return false;
        }

        var result = await processRunner.RunAsync(
                environmentDiagnosis.WinGet.ExecutablePath,
                [
                    "install",
                    "--id",
                    dependency.PackageId,
                    "--exact",
                    "--source",
                    "winget",
                    "--silent",
                    "--accept-package-agreements",
                    "--accept-source-agreements",
                    "--force",
                ],
                DependencyInstallTimeout,
                cancellationToken);

        if (result.ExitCode is not 0)
        {
            var message =
                $"{displayName} installation via WinGet failed. Exit code: {result.ExitCode}.";
            SetRollbackInfo(message, false);
            finish.Progress = message + Environment.NewLine + (finish.Progress ?? string.Empty);
            return false;
        }

        return true;
    }
}
