using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace LocalAi.Installer.Core.Diagnosis;

public enum SupportStatus
{
    Supported,
    Unsupported,
}

public enum ObservationState
{
    Available,
    Unavailable,
    Unknown,
    Failed,
    Unsupported,
}

public enum DependencyState
{
    Detected,
    NotFound,
    Failed,
}

public enum ExistingLocalAiState
{
    Absent,
    Compatible,
    Unrecognized,
}

public enum AgentKind
{
    Codex,
    Claude,
}

public sealed record HostEnvironmentSnapshot(
    bool IsWindows,
    string ProductName,
    Version Version,
    Architecture Architecture);

public sealed record OperatingSystemSnapshot(
    string ProductName,
    Version Version,
    Architecture Architecture,
    SupportStatus OperatingSystemSupport,
    SupportStatus ArchitectureSupport);

public sealed record DiskSnapshot(
    ObservationState State,
    long? AvailableBytes,
    string? Reason);

public sealed record NetworkSnapshot(
    ObservationState State,
    string? Reason);

public sealed record DependencySnapshot(
    string Name,
    DependencyState State,
    string? ExecutablePath,
    string? Version,
    string? Reason);

public sealed record GpuAdapterSnapshot(
    string StableId,
    string Name,
    ulong DedicatedLocalBytes,
    bool IsSoftware);

public sealed record GpuSnapshot
{
    public GpuSnapshot(
        ObservationState state,
        IEnumerable<GpuAdapterSnapshot> adapters,
        string? reason)
    {
        State = state;
        Adapters = Snapshot(adapters);
        Reason = reason;
    }

    public ObservationState State { get; }
    public IReadOnlyList<GpuAdapterSnapshot> Adapters { get; }
    public string? Reason { get; }

    private static ReadOnlyCollection<GpuAdapterSnapshot> Snapshot(
        IEnumerable<GpuAdapterSnapshot> source) =>
        Array.AsReadOnly((source ?? throw new ArgumentNullException(nameof(source))).ToArray());
}

public sealed record ExistingLocalAiSnapshot(
    ExistingLocalAiState State,
    string? Version,
    string? VersionPath,
    string? Reason);

public sealed record FileMetadataSnapshot(
    string Path,
    bool Exists,
    long? SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string? Version)
{
    public static FileMetadataSnapshot Absent(string path) =>
        new(path, false, null, null, null);
}

public sealed record AgentSnapshot(
    AgentKind Kind,
    FileMetadataSnapshot Executable,
    FileMetadataSnapshot Config,
    FileMetadataSnapshot Instructions)
{
    public static AgentSnapshot Absent(AgentKind kind) =>
        new(
            kind,
            FileMetadataSnapshot.Absent(string.Empty),
            FileMetadataSnapshot.Absent(string.Empty),
            FileMetadataSnapshot.Absent(string.Empty));
}

public sealed record InstalledApplicationMetadata(
    string DisplayName,
    string? DisplayVersion,
    string? InstallLocation,
    string? ExecutablePath,
    string? ExecutableVersion,
    string? DetectedVersion = null);

public sealed record EnvironmentDiagnosis
{
    public EnvironmentDiagnosis(
        OperatingSystemSnapshot operatingSystem,
        DiskSnapshot disk,
        NetworkSnapshot network,
        DependencySnapshot winGet,
        DependencySnapshot git,
        DependencySnapshot gitHubCli,
        DependencySnapshot ollama,
        GpuSnapshot gpu,
        ExistingLocalAiSnapshot existingLocalAi,
        IEnumerable<AgentSnapshot> agents,
        IEnumerable<string> unsupportedReasons)
    {
        OperatingSystem = operatingSystem;
        Disk = disk;
        Network = network;
        WinGet = winGet;
        Git = git;
        GitHubCli = gitHubCli;
        Ollama = ollama;
        Gpu = new GpuSnapshot(gpu.State, gpu.Adapters, gpu.Reason);
        ExistingLocalAi = existingLocalAi;
        Agents = Array.AsReadOnly(
            (agents ?? throw new ArgumentNullException(nameof(agents))).ToArray());
        UnsupportedReasons = Array.AsReadOnly(
            (unsupportedReasons ?? throw new ArgumentNullException(nameof(unsupportedReasons))).ToArray());
    }

    public OperatingSystemSnapshot OperatingSystem { get; }
    public DiskSnapshot Disk { get; }
    public NetworkSnapshot Network { get; }
    public DependencySnapshot WinGet { get; }
    public DependencySnapshot Git { get; }

    /// <summary>
    /// The GitHub CLI is a required dependency, because the release repository is private and
    /// the installer reads it through an existing `gh auth login` rather than handling a token.
    /// It used to be probed separately from every other dependency, by a helper that returned a
    /// bare bool — so when it came back false there was no path, no version and no reason to
    /// show, and "GitHub CLI not found" was unanswerable on a machine where `gh --version`
    /// plainly worked. It now travels with the rest of the diagnosis.
    /// </summary>
    public DependencySnapshot GitHubCli { get; }
    public DependencySnapshot Ollama { get; }
    public GpuSnapshot Gpu { get; }
    public ExistingLocalAiSnapshot ExistingLocalAi { get; }
    public IReadOnlyList<AgentSnapshot> Agents { get; }
    public IReadOnlyList<string> UnsupportedReasons { get; }
    public bool IsSupported =>
        OperatingSystem.OperatingSystemSupport == SupportStatus.Supported &&
        OperatingSystem.ArchitectureSupport == SupportStatus.Supported &&
        UnsupportedReasons.Count == 0;
}
