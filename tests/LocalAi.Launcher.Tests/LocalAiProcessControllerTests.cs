namespace LocalAi.Launcher.Tests;

public sealed class LocalAiProcessControllerTests
{
    [Fact]
    public void Selects_only_exact_executables_and_broker_assemblies_below_version()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalAi", "bin", "versions");
        var v1 = Path.Combine(root, "v1");
        var v2 = Path.Combine(root, "v2");
        var started = DateTimeOffset.UtcNow;
        var snapshots = new[]
        {
            new ProcessSnapshot(
                10,
                started,
                Path.Combine(v1, "codesearch-mcp.exe"),
                null),
            new ProcessSnapshot(
                11,
                started,
                Environment.GetEnvironmentVariable("ComSpec")!,
                Path.Combine(v1, "LocalAi.Broker.dll")),
            new ProcessSnapshot(
                12,
                started,
                Path.Combine(Path.GetTempPath(), "ollama.exe"),
                null),
            new ProcessSnapshot(
                13,
                started,
                Environment.GetEnvironmentVariable("ComSpec")!,
                Path.Combine(Path.GetTempPath(), "Unrelated.dll")),
            new ProcessSnapshot(
                14,
                started,
                Path.Combine(v2, "locallm-mcp.exe"),
                null)
        };
        var controller = new LocalAiProcessController(
            () => snapshots,
            static (_, _) => { });

        var selected = controller.SelectOwnedByVersion(v1, snapshots);

        Assert.Equal([10, 11], selected.Select(process => process.ProcessId));
    }

    [Fact]
    public void Stop_uses_only_selected_snapshots()
    {
        var version = Path.Combine(
            Path.GetTempPath(),
            "LocalAi",
            "bin",
            "versions",
            "v1");
        var selected = new ProcessSnapshot(
            10,
            DateTimeOffset.UtcNow,
            Path.Combine(version, "localai.exe"),
            null);
        var unrelated = new ProcessSnapshot(
            11,
            DateTimeOffset.UtcNow,
            Path.Combine(Path.GetTempPath(), "ollama.exe"),
            null);
        var stopped = new List<int>();
        var controller = new LocalAiProcessController(
            () => [selected, unrelated],
            (snapshot, _) => stopped.Add(snapshot.ProcessId));

        controller.StopOwnedByVersion(version, TimeSpan.Zero);

        Assert.Equal([10], stopped);
    }
}
