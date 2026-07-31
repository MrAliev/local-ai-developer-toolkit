using System.Runtime.InteropServices;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Tests;

public sealed class WindowsEnvironmentDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Detector.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Windows 10 Pro")]
    [InlineData("Windows 11 Pro")]
    public async Task Accepts_Windows_10_and_11_x64(string productName)
    {
        var fixture = CreateFixture(productName, Architecture.X64, isWindows: true);

        var diagnosis = await fixture.Detector.DetectAsync(TestContext.Current.CancellationToken);

        Assert.True(diagnosis.IsSupported);
        Assert.Equal(SupportStatus.Supported, diagnosis.OperatingSystem.OperatingSystemSupport);
        Assert.Equal(SupportStatus.Supported, diagnosis.OperatingSystem.ArchitectureSupport);
    }

    [Theory]
    [InlineData("Linux", Architecture.X64, false)]
    [InlineData("Windows 11 Pro", Architecture.Arm64, true)]
    [InlineData("Windows Server 2025", Architecture.X64, true)]
    public async Task Rejects_unsupported_operating_system_or_architecture(
        string productName,
        Architecture architecture,
        bool isWindows)
    {
        var fixture = CreateFixture(productName, architecture, isWindows);

        var diagnosis = await fixture.Detector.DetectAsync(TestContext.Current.CancellationToken);

        Assert.False(diagnosis.IsSupported);
        Assert.NotEmpty(diagnosis.UnsupportedReasons);
    }

    [Fact]
    public async Task Represents_disk_and_network_failures_without_inventing_values()
    {
        var fixture = CreateFixture();
        fixture.Disk.Result = new DiskSnapshot(ObservationState.Unknown, null, "Drive unavailable.");
        fixture.Network.Result = new NetworkSnapshot(ObservationState.Failed, "Probe failed.");

        var diagnosis = await fixture.Detector.DetectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ObservationState.Unknown, diagnosis.Disk.State);
        Assert.Null(diagnosis.Disk.AvailableBytes);
        Assert.Equal(ObservationState.Failed, diagnosis.Network.State);
    }

    [Fact]
    public async Task Detects_WinGet_and_Git_with_exact_bounded_version_calls()
    {
        var fixture = CreateFixture();
        fixture.Environment.Executables["winget.exe"] = @"C:\Tools\winget.exe";
        fixture.Environment.Executables["git.exe"] = @"C:\Tools\git.exe";
        fixture.Process.Results[@"C:\Tools\winget.exe"] =
            new ProcessResult(0, "v1.10.0", "", false, false);
        fixture.Process.Results[@"C:\Tools\git.exe"] =
            new ProcessResult(0, "git version 2.50.1.windows.1", "", false, false);

        var diagnosis = await fixture.Detector.DetectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("v1.10.0", diagnosis.WinGet.Version);
        Assert.Equal("git version 2.50.1.windows.1", diagnosis.Git.Version);
        Assert.Collection(
            fixture.Process.Calls,
            call =>
            {
                Assert.Equal(@"C:\Tools\winget.exe", call.Executable);
                Assert.Equal(["--version"], call.Arguments);
                Assert.InRange(call.Timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
            },
            call =>
            {
                Assert.Equal(@"C:\Tools\git.exe", call.Executable);
                Assert.Equal(["--version"], call.Arguments);
                Assert.InRange(call.Timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
            });
    }

    [Fact]
    public async Task Detects_Ollama_only_from_registry_and_file_metadata()
    {
        var fixture = CreateFixture();
        fixture.Installed.Ollama = new InstalledApplicationMetadata(
            "Ollama",
            "0.11.4",
            @"C:\Users\me\AppData\Local\Programs\Ollama",
            @"C:\Users\me\AppData\Local\Programs\Ollama\ollama.exe",
            "0.11.4.0");

        var diagnosis = await fixture.Detector.DetectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DependencyState.Detected, diagnosis.Ollama.State);
        Assert.Equal("0.11.4.0", diagnosis.Ollama.Version);
        Assert.Equal(
            @"C:\Users\me\AppData\Local\Programs\Ollama\ollama.exe",
            diagnosis.Ollama.ExecutablePath);
        Assert.DoesNotContain(
            fixture.Process.Calls,
            call =>
                call.Executable.Contains("ollama", StringComparison.OrdinalIgnoreCase) ||
                call.Arguments.Any(argument =>
                    argument.Contains("ollama", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Detects_agent_paths_and_metadata_without_reading_config_contents()
    {
        Directory.CreateDirectory(_root);
        var fixture = CreateFixture();
        fixture.Environment.Executables["codex.exe"] = Path.Combine(_root, "codex.exe");
        fixture.Environment.Executables["claude.exe"] = Path.Combine(_root, "claude.exe");
        fixture.Files.Metadata[Path.Combine(_root, "codex.exe")] =
            new FileMetadataSnapshot(Path.Combine(_root, "codex.exe"), true, 10, DateTimeOffset.UnixEpoch, "1.2.3");
        fixture.Files.Metadata[Path.Combine(_root, "claude.exe")] =
            new FileMetadataSnapshot(Path.Combine(_root, "claude.exe"), true, 11, DateTimeOffset.UnixEpoch, "4.5.6");
        fixture.Files.Metadata[Path.Combine(_root, ".codex", "config.toml")] =
            new FileMetadataSnapshot(Path.Combine(_root, ".codex", "config.toml"), true, 20, DateTimeOffset.UnixEpoch, null);
        fixture.Files.Metadata[Path.Combine(_root, ".codex", "AGENTS.md")] =
            new FileMetadataSnapshot(Path.Combine(_root, ".codex", "AGENTS.md"), true, 21, DateTimeOffset.UnixEpoch, null);
        fixture.Files.Metadata[Path.Combine(_root, ".claude.json")] =
            new FileMetadataSnapshot(Path.Combine(_root, ".claude.json"), true, 22, DateTimeOffset.UnixEpoch, null);
        fixture.Files.Metadata[Path.Combine(_root, ".claude", "CLAUDE.md")] =
            new FileMetadataSnapshot(Path.Combine(_root, ".claude", "CLAUDE.md"), true, 23, DateTimeOffset.UnixEpoch, null);

        var diagnosis = await fixture.Detector.DetectAsync(TestContext.Current.CancellationToken);

        Assert.Collection(
            diagnosis.Agents.OrderBy(agent => agent.Kind),
            codex =>
            {
                Assert.Equal(AgentKind.Codex, codex.Kind);
                Assert.Equal("1.2.3", codex.Executable.Version);
                Assert.Equal(20, codex.Config.SizeBytes);
                Assert.Equal(21, codex.Instructions.SizeBytes);
            },
            claude =>
            {
                Assert.Equal(AgentKind.Claude, claude.Kind);
                Assert.Equal("4.5.6", claude.Executable.Version);
                Assert.Equal(22, claude.Config.SizeBytes);
                Assert.Equal(23, claude.Instructions.SizeBytes);
            });
        Assert.Empty(fixture.Files.ReadPaths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DetectorFixture CreateFixture(
        string productName = "Windows 11 Pro",
        Architecture architecture = Architecture.X64,
        bool isWindows = true)
    {
        var environment = new FakeEnvironmentProbe(
            new HostEnvironmentSnapshot(
                isWindows,
                productName,
                new Version(10, 0, 26100),
                architecture),
            _root,
            _root);
        var files = new FakeFileSystemProbe();
        var process = new RecordingProcessRunner();
        var installed = new FakeInstalledApplicationProbe();
        var disk = new FakeDiskProbe();
        var network = new FakeNetworkProbe();
        var gpu = new FakeGpuProbe();
        var detector = new WindowsEnvironmentDetector(
            environment,
            files,
            process,
            installed,
            disk,
            network,
            gpu);
        return new DetectorFixture(
            detector,
            environment,
            files,
            process,
            installed,
            disk,
            network);
    }

    private sealed record DetectorFixture(
        WindowsEnvironmentDetector Detector,
        FakeEnvironmentProbe Environment,
        FakeFileSystemProbe Files,
        RecordingProcessRunner Process,
        FakeInstalledApplicationProbe Installed,
        FakeDiskProbe Disk,
        FakeNetworkProbe Network);

    private sealed class FakeEnvironmentProbe(
        HostEnvironmentSnapshot host,
        string localAppData,
        string userProfile) : IEnvironmentProbe
    {
        public Dictionary<string, string> Executables { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HostEnvironmentSnapshot GetHost() => host;
        public string LocalAppData => localAppData;
        public string UserProfile => userProfile;
        public string? ResolveExecutable(string executableName) =>
            Executables.GetValueOrDefault(executableName);
    }

    private sealed class FakeFileSystemProbe : IFileSystemProbe
    {
        public Dictionary<string, FileMetadataSnapshot> Metadata { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<string> ReadPaths { get; } = [];

        public bool FileExists(string path) => Metadata.GetValueOrDefault(path)?.Exists == true;
        public bool DirectoryExists(string path) => false;
        public string ReadAllText(string path)
        {
            ReadPaths.Add(path);
            throw new InvalidOperationException("Detector must not read agent configuration contents.");
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);
        public string ResolvePhysicalPath(string path) => Path.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) =>
            Metadata.GetValueOrDefault(path) ?? FileMetadataSnapshot.Absent(path);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public Dictionary<string, ProcessResult> Results { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new ProcessCall(executable, arguments.ToArray(), timeout));
            return Task.FromResult(
                Results.GetValueOrDefault(executable) ??
                new ProcessResult(1, "", "not configured", false, false));
        }
    }

    private sealed record ProcessCall(
        string Executable,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout);

    private sealed class FakeInstalledApplicationProbe : IInstalledApplicationProbe
    {
        public InstalledApplicationMetadata? Ollama { get; set; }
        public Task<InstalledApplicationMetadata?> FindOllamaAsync(
            CancellationToken cancellationToken) => Task.FromResult(Ollama);
    }

    private sealed class FakeDiskProbe : IDiskProbe
    {
        public DiskSnapshot Result { get; set; } =
            new(ObservationState.Available, 1_000_000, null);
        public DiskSnapshot Observe(string path) => Result;
    }

    private sealed class FakeNetworkProbe : INetworkProbe
    {
        public NetworkSnapshot Result { get; set; } =
            new(ObservationState.Available, null);
        public Task<NetworkSnapshot> ObserveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }

    private sealed class FakeGpuProbe : IWindowsGpuProbe
    {
        public Task<GpuSnapshot> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GpuSnapshot(ObservationState.Available, [], null));
    }
}
