using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Tests;

public sealed class DiagnosisSnapshotTests
{
    [Fact]
    public void Environment_diagnosis_defensively_snapshots_input_collections()
    {
        var adapters = new List<GpuAdapterSnapshot>
        {
            new("gpu-1", "GPU 1", 8_000, false),
        };
        var agents = new List<AgentSnapshot>();
        var reasons = new List<string>();
        var diagnosis = new EnvironmentDiagnosis(
            new OperatingSystemSnapshot(
                "Windows 11 Pro",
                new Version(10, 0, 26100),
                System.Runtime.InteropServices.Architecture.X64,
                SupportStatus.Supported,
                SupportStatus.Supported),
            new DiskSnapshot(ObservationState.Available, 100_000, null),
            new NetworkSnapshot(ObservationState.Available, null),
            new DependencySnapshot("WinGet", DependencyState.Detected, "winget.exe", "1.10", null),
            new DependencySnapshot("Git", DependencyState.Detected, "git.exe", "2.50", null),
            new DependencySnapshot("GitHubCli", DependencyState.Detected, "gh.exe", "2.97", null),
            new DependencySnapshot("Ollama", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("DotNetSdk", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("NodeJs", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Npm", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("ScipTypeScript", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Python", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("ScipPython", DependencyState.NotFound, null, null, null),
            new GpuSnapshot(ObservationState.Available, adapters, null),
            new ExistingLocalAiSnapshot(ExistingLocalAiState.Absent, null, null, null),
            agents,
            reasons);

        adapters.Add(new GpuAdapterSnapshot("gpu-2", "GPU 2", 16_000, false));
        agents.Add(AgentSnapshot.Absent(AgentKind.Codex));
        reasons.Add("late mutation");

        Assert.Single(diagnosis.Gpu.Adapters);
        Assert.Empty(diagnosis.Agents);
        Assert.Empty(diagnosis.UnsupportedReasons);
        Assert.True(diagnosis.IsSupported);
        Assert.Throws<NotSupportedException>(
            () => ((IList<GpuAdapterSnapshot>)diagnosis.Gpu.Adapters).Add(
                new GpuAdapterSnapshot("gpu-3", "GPU 3", 1, false)));
    }

    [Fact]
    public void Environment_diagnosis_cannot_report_supported_when_platform_is_unsupported()
    {
        var diagnosis = new EnvironmentDiagnosis(
            new OperatingSystemSnapshot(
                "Windows 11",
                new Version(10, 0),
                System.Runtime.InteropServices.Architecture.Arm64,
                SupportStatus.Supported,
                SupportStatus.Unsupported),
            new DiskSnapshot(ObservationState.Unknown, null, null),
            new NetworkSnapshot(ObservationState.Unknown, null),
            new DependencySnapshot("WinGet", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Git", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("GitHubCli", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Ollama", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("DotNetSdk", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("NodeJs", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Npm", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("ScipTypeScript", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Python", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("ScipPython", DependencyState.NotFound, null, null, null),
            new GpuSnapshot(ObservationState.Unknown, [], null),
            new ExistingLocalAiSnapshot(ExistingLocalAiState.Absent, null, null, null),
            [],
            []);

        Assert.False(diagnosis.IsSupported);
    }
}
