using System.Collections.ObjectModel;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Shows what was actually found on the machine. The detector already collects the operating
/// system, disk, network, WinGet, Git, Ollama and every graphics adapter, so the page reports
/// all of it rather than reducing the environment to a single boolean.
/// </summary>
public sealed class DiagnosePageViewModel : ObservableObject
{
    private bool isSupported;
    private string? unsupportedReason;
    private bool hasUsableAdapter;
    private bool hasGitHubSignIn;
    private bool isChecking = true;

    public ObservableCollection<EnvironmentCheck> Checks { get; } = [];

    /// <summary>
    /// True while the environment is being probed. Detection launches winget, git and ollama
    /// and takes several seconds; without this the first page sat empty and looked frozen.
    /// </summary>
    public bool IsChecking
    {
        get => isChecking;
        set
        {
            SetProperty(ref isChecking, value);
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public bool HasResults => !IsChecking;

    public bool IsSupported
    {
        get => isSupported;
        set
        {
            SetProperty(ref isSupported, value);
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public string? UnsupportedReason
    {
        get => unsupportedReason;
        set => SetProperty(ref unsupportedReason, value);
    }

    /// <summary>
    /// Drives the hint on the residency page. A missing adapter never blocks installation —
    /// it only means the strict residency policy would refuse to load any model.
    /// </summary>
    public bool HasUsableAdapter
    {
        get => hasUsableAdapter;
        private set => SetProperty(ref hasUsableAdapter, value);
    }

    /// <summary>
    /// Drives the hint on the package page. A missing sign-in never blocks installation —
    /// prerequisites and client integration are still worth applying — but it does decide
    /// whether the release can be read at all.
    /// </summary>
    public bool HasGitHubSignIn
    {
        get => hasGitHubSignIn;
        private set => SetProperty(ref hasGitHubSignIn, value);
    }

    /// <summary>
    /// Blocked while the check runs: continuing on results that are not in yet would show
    /// the next pages an environment nobody has looked at.
    /// </summary>
    public bool CanContinue => IsSupported && !IsChecking;

    public void SetResult(bool supported, string? reason = null)
    {
        IsSupported = supported;
        UnsupportedReason = supported ? null : reason;
    }

    public void Load(EnvironmentDiagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);

        Checks.Clear();
        Checks.Add(new EnvironmentCheck(
            "Operating system",
            diagnosis.OperatingSystem.OperatingSystemSupport == SupportStatus.Supported &&
            diagnosis.OperatingSystem.ArchitectureSupport == SupportStatus.Supported
                ? CheckStatus.Ok
                : CheckStatus.Blocking,
            $"{diagnosis.OperatingSystem.ProductName} ({diagnosis.OperatingSystem.Architecture})"));

        Checks.Add(Describe(
            diagnosis.WinGet,
            "Needed to install Git and Ollama automatically."));
        Checks.Add(Describe(
            diagnosis.Git,
            "Needed to index repositories."));
        Checks.Add(Describe(
            diagnosis.GitHubCli,
            "Reads the private release repository through your existing 'gh auth login'."));

        // Its own line, and a warning rather than "not found": the CLI can be installed from
        // the next page, the sign-in cannot. Someone whose package step is about to fail
        // needs to read that here, where the wizard is still describing the machine, not two
        // pages later as "could not determine the newest release".
        HasGitHubSignIn = diagnosis.GitHubSignIn.State == DependencyState.Detected;
        Checks.Add(new EnvironmentCheck(
            "GitHub sign-in",
            HasGitHubSignIn ? CheckStatus.Ok : CheckStatus.Warning,
            HasGitHubSignIn
                ? diagnosis.GitHubSignIn.Version ?? "signed in"
                : diagnosis.GitHubSignIn.Reason ??
                    "Not signed in. Run 'gh auth login' in a terminal."));
        Checks.Add(Describe(
            diagnosis.Ollama,
            "Runs the local models."));
        Checks.Add(Describe(
            diagnosis.DotNetSdk,
            "Loads C# solutions and restores project dependencies for exact navigation."));
        Checks.Add(Describe(
            diagnosis.NodeJs,
            "Runs the TypeScript semantic indexer."));
        Checks.Add(Describe(
            diagnosis.Npm,
            "Installs the pinned TypeScript semantic indexer."));
        Checks.Add(Describe(
            diagnosis.ScipTypeScript,
            "Provides exact TypeScript and JavaScript navigation."));
        Checks.Add(Describe(
            diagnosis.Python,
            "Runs the Python semantic indexer."));
        Checks.Add(Describe(
            diagnosis.ScipPython,
            "Provides exact Python navigation."));

        var usableAdapters = diagnosis.Gpu.Adapters
            .Where(adapter => !adapter.IsSoftware)
            .ToArray();
        HasUsableAdapter = usableAdapters.Any(adapter => adapter.DedicatedLocalBytes > 0);
        Checks.Add(new EnvironmentCheck(
            "Graphics adapters",
            HasUsableAdapter ? CheckStatus.Ok : CheckStatus.Warning,
            diagnosis.Gpu.Adapters.Count > 0
                ? string.Join("; ", diagnosis.Gpu.Adapters.Select(Describe))
                : diagnosis.Gpu.Reason ?? "No adapter reported."));

        Checks.Add(new EnvironmentCheck(
            "Free disk space",
            diagnosis.Disk.AvailableBytes is > 0 and var free && free >= 8L * 1024 * 1024 * 1024
                ? CheckStatus.Ok
                : CheckStatus.Warning,
            diagnosis.Disk.AvailableBytes is { } bytes
                ? $"{bytes / (1024d * 1024 * 1024):N1} GB available"
                : diagnosis.Disk.Reason ?? "unknown"));

        Checks.Add(new EnvironmentCheck(
            "Network",
            diagnosis.Network.State == ObservationState.Available
                ? CheckStatus.Ok
                : CheckStatus.Warning,
            diagnosis.Network.State == ObservationState.Available
                ? "reachable"
                : diagnosis.Network.Reason ?? "not reachable"));

        Checks.Add(new EnvironmentCheck(
            "Existing LocalAi",
            diagnosis.ExistingLocalAi.State switch
            {
                ExistingLocalAiState.Absent => CheckStatus.Ok,
                ExistingLocalAiState.Compatible => CheckStatus.Ok,
                _ => CheckStatus.Warning,
            },
            diagnosis.ExistingLocalAi.State switch
            {
                ExistingLocalAiState.Absent => "none — this will be a first installation",
                ExistingLocalAiState.Compatible =>
                    $"version {diagnosis.ExistingLocalAi.Version} at " +
                    $"{diagnosis.ExistingLocalAi.VersionPath}",
                _ => diagnosis.ExistingLocalAi.Reason ?? "present but not recognised",
            }));

        SetResult(
            diagnosis.IsSupported,
            diagnosis.UnsupportedReasons.Count == 0
                ? null
                : string.Join("; ", diagnosis.UnsupportedReasons));

        OnPropertyChanged(nameof(Checks));
    }

    private static string Describe(GpuAdapterSnapshot adapter)
    {
        var memory = adapter.DedicatedLocalBytes > 0
            ? $" ({adapter.DedicatedLocalBytes / (1024d * 1024 * 1024):N1} GB dedicated)"
            : " (no dedicated memory)";
        return adapter.Name + memory + (adapter.IsSoftware ? " [software]" : string.Empty);
    }

    private static EnvironmentCheck Describe(DependencySnapshot snapshot, string purpose) =>
        new(
            snapshot.Name,
            snapshot.State == DependencyState.Detected ? CheckStatus.Ok : CheckStatus.Missing,
            snapshot.State == DependencyState.Detected
                ? string.Join(
                    " — ",
                    new[] { snapshot.Version, snapshot.ExecutablePath }
                        .Where(part => !string.IsNullOrWhiteSpace(part)))
                : snapshot.Reason is { Length: > 0 } reason
                    ? $"{purpose} ({reason})"
                    : purpose);
}
