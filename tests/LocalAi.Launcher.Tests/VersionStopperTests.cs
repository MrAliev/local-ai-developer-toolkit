using LocalAi.Launcher;

namespace LocalAi.Launcher.Tests;

public sealed class VersionStopperTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAiVersionStopperTests",
        Guid.NewGuid().ToString("N"));

    public VersionStopperTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string BinRoot => Path.Combine(root, "bin");

    private string PublishVersion(string version)
    {
        var directory = Path.Combine(BinRoot, "versions", version);
        Directory.CreateDirectory(directory);
        foreach (var file in LauncherLayout.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(directory, file), "binary");
        }

        return directory;
    }

    private void WritePointer(string version)
    {
        Directory.CreateDirectory(BinRoot);
        File.WriteAllText(
            Path.Combine(BinRoot, "current.json"),
            $"{{\"schemaVersion\":1,\"version\":\"{version}\"}}");
    }

    private static LocalAiProcessController Controller(
        IReadOnlyList<ProcessSnapshot> running,
        List<int> stopped) =>
        new(() => running, (process, _) => stopped.Add(process.ProcessId));

    [Fact]
    public void Stops_the_tools_of_the_active_version_without_touching_the_pointer()
    {
        var directory = PublishVersion("v1");
        WritePointer("v1");
        var stopped = new List<int>();
        var running = new[]
        {
            new ProcessSnapshot(41, DateTimeOffset.UtcNow, Path.Combine(directory, "codesearch-mcp.exe"), null),
            new ProcessSnapshot(42, DateTimeOffset.UtcNow, Path.Combine(directory, "locallm-mcp.exe"), null),
            new ProcessSnapshot(43, DateTimeOffset.UtcNow, @"C:\elsewhere\unrelated.exe", null),
        };
        var pointerBefore = File.ReadAllBytes(Path.Combine(BinRoot, "current.json"));

        var result = new VersionStopper(BinRoot, Controller(running, stopped), TimeSpan.FromSeconds(5))
            .Stop(null);

        Assert.Equal("v1", result.Version);
        Assert.True(result.StoppedAnything);
        Assert.Equal([41, 42], stopped);
        // Stopping is not activating: the pointer is somebody else's business.
        Assert.Equal(pointerBefore, File.ReadAllBytes(Path.Combine(BinRoot, "current.json")));
    }

    [Fact]
    public void A_machine_without_a_pointer_has_nothing_to_stop()
    {
        Directory.CreateDirectory(BinRoot);
        var stopped = new List<int>();

        var result = new VersionStopper(BinRoot, Controller([], stopped), TimeSpan.FromSeconds(5))
            .Stop(null);

        // A first installation must not have to explain an error it cannot act on.
        Assert.Null(result.Version);
        Assert.False(result.StoppedAnything);
        Assert.Empty(stopped);
    }

    [Fact]
    public void An_explicit_version_stops_that_one_rather_than_the_active_one()
    {
        var old = PublishVersion("v1");
        PublishVersion("v2");
        WritePointer("v2");
        var stopped = new List<int>();
        var running = new[]
        {
            new ProcessSnapshot(51, DateTimeOffset.UtcNow, Path.Combine(old, "localai.exe"), null),
        };

        var result = new VersionStopper(BinRoot, Controller(running, stopped), TimeSpan.FromSeconds(5))
            .Stop("v1");

        Assert.Equal("v1", result.Version);
        Assert.Equal([51], stopped);
    }

    [Fact]
    public void Nothing_running_is_reported_as_nothing_stopped()
    {
        PublishVersion("v1");
        WritePointer("v1");
        var stopped = new List<int>();

        var result = new VersionStopper(BinRoot, Controller([], stopped), TimeSpan.FromSeconds(5))
            .Stop(null);

        Assert.Equal("v1", result.Version);
        Assert.False(result.StoppedAnything);
        Assert.Empty(stopped);
    }
}
