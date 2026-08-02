using LocalAi.Contracts;
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

    private VersionStopper Stopper(LocalAiProcessController controller) =>
        new(
            BinRoot,
            controller,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            // No real waiting: the fakes decide when the broker is gone.
            _ => { });

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

        var result = Stopper(Controller(running, stopped)).Stop(null);

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

        var result = Stopper(Controller([], stopped)).Stop(null);

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

        var result = Stopper(Controller(running, stopped)).Stop("v1");

        Assert.Equal("v1", result.Version);
        Assert.Equal([51], stopped);
    }

    [Fact]
    public void The_broker_is_asked_to_finish_before_anything_is_killed()
    {
        var directory = PublishVersion("v1");
        WritePointer("v1");
        var startedAt = DateTimeOffset.UtcNow;
        var brokerPath = Path.Combine(directory, "LocalAi.Broker.exe");
        var broker = new ProcessSnapshot(61, startedAt, brokerPath, brokerPath);
        var tool = new ProcessSnapshot(
            62,
            startedAt,
            Path.Combine(directory, "codesearch-mcp.exe"),
            null);
        var stopped = new List<int>();
        BrokerShutdownRequest? seenByBroker = null;
        // Stands in for the broker's heartbeat: it reads the request and exits by itself, which
        // is what a stop should look like when nothing has to be destroyed to achieve it.
        var controller = new LocalAiProcessController(
            () =>
            {
                seenByBroker ??= BrokerShutdownRequestStore.Read(root);
                return seenByBroker is null
                    ? [broker, tool]
                    : [tool];
            },
            (process, _) => stopped.Add(process.ProcessId));

        var result = Stopper(controller).Stop(null);

        Assert.True(result.BrokerDrained);
        Assert.Equal(61, seenByBroker?.ProcessId);
        Assert.Equal(startedAt, seenByBroker?.StartedAtUtc);
        // The broker left on its own; only the stdio tool, which has no channel to be asked
        // through and nothing to lose, was terminated.
        Assert.Equal([62], stopped);
        // And the request does not outlive the stop, or it would shut down the next broker.
        Assert.Null(BrokerShutdownRequestStore.Read(root));
    }

    [Fact]
    public void A_broker_that_will_not_finish_is_still_stopped()
    {
        var directory = PublishVersion("v1");
        WritePointer("v1");
        var brokerPath = Path.Combine(directory, "LocalAi.Broker.exe");
        var broker = new ProcessSnapshot(71, DateTimeOffset.UtcNow, brokerPath, brokerPath);
        var stopped = new List<int>();

        var result = Stopper(Controller([broker], stopped)).Stop(null);

        // Asking is the first answer, not the only one.
        Assert.False(result.BrokerDrained);
        Assert.True(result.StoppedAnything);
        Assert.Equal([71], stopped);
        Assert.Null(BrokerShutdownRequestStore.Read(root));
    }

    [Fact]
    public void Nothing_running_is_reported_as_nothing_stopped()
    {
        PublishVersion("v1");
        WritePointer("v1");
        var stopped = new List<int>();

        var result = Stopper(Controller([], stopped)).Stop(null);

        Assert.Equal("v1", result.Version);
        Assert.False(result.StoppedAnything);
        Assert.Empty(stopped);
    }
}
