using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerRuntimeStateStoreTests
{
    [Fact]
    public void Publish_replaces_state_with_strict_readable_document()
    {
        using var fixture = new RuntimeStateFixture();
        var state = CurrentState();

        fixture.Store.Publish(state);

        var actual = JsonSerializer.Deserialize<BrokerProcessState>(
            File.ReadAllText(fixture.StatePath),
            LocalAiJson.Strict);
        Assert.Equal(state, actual);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public void Publish_includes_current_protocol_and_build_compatibility()
    {
        using var fixture = new RuntimeStateFixture();

        fixture.Store.Publish(CurrentState());

        var actual = JsonSerializer.Deserialize<BrokerProcessState>(
            File.ReadAllText(fixture.StatePath),
            LocalAiJson.Strict);
        Assert.Equal(BrokerCompatibilityContract.Current, actual!.Compatibility);
        Assert.Equal(
            BrokerCompatibilityContract.HostStateSchemaVersion,
            actual.SchemaVersion);
    }

    [Fact]
    public async Task Publish_retries_when_existing_state_is_temporarily_locked()
    {
        using var fixture = new RuntimeStateFixture();
        var initial = CurrentState() with
        {
            HeartbeatAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        var heartbeat = initial with { HeartbeatAtUtc = DateTimeOffset.UtcNow };
        fixture.Store.Publish(initial);
        var locked = new FileStream(
            fixture.StatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(75),
                    TestContext.Current.CancellationToken);
                locked.Dispose();
            },
            TestContext.Current.CancellationToken);

        try
        {
            // A budget far larger than the release delay, so this asserts that the retry works
            // rather than that the machine was idle enough to finish inside the default.
            fixture.Store.Publish(heartbeat, TimeSpan.FromSeconds(30));
            await release;
        }
        finally
        {
            locked.Dispose();
        }

        var actual = JsonSerializer.Deserialize<BrokerProcessState>(
            File.ReadAllText(fixture.StatePath),
            LocalAiJson.Strict);
        Assert.Equal(heartbeat, actual);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public void Delete_owned_state_preserves_replacement_host()
    {
        using var fixture = new RuntimeStateFixture();
        var owner = CurrentState();
        var replacement = owner with { ProcessId = 99 };
        fixture.Store.Publish(replacement);

        fixture.Store.DeleteIfOwnedBy(owner);

        Assert.True(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Delete_owned_state_removes_matching_host()
    {
        using var fixture = new RuntimeStateFixture();
        var owner = CurrentState();
        fixture.Store.Publish(owner);

        fixture.Store.DeleteIfOwnedBy(owner);

        Assert.False(File.Exists(fixture.StatePath));
    }

    private static BrokerProcessState CurrentState(int processId = 42) =>
        new(
            processId,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            BrokerCompatibilityContract.HostStateSchemaVersion,
            Path.GetFullPath("LocalAi.Broker.dll"),
            BrokerCompatibilityContract.Current);

    private sealed class RuntimeStateFixture : IDisposable
    {
        public RuntimeStateFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "localai-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new BrokerRuntimeStateStore(Root);
        }

        public string Root { get; }

        public string StatePath => Path.Combine(Root, "host.json");

        public BrokerRuntimeStateStore Store { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
