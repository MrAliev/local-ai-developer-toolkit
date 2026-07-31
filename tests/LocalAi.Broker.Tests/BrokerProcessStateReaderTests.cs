using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerProcessStateReaderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly string BrokerAssemblyPath =
        Path.GetFullPath("LocalAi.Broker.dll");

    public static TheoryData<string> MalformedStateDocuments =>
        new()
        {
            """{"ProcessId":42""",
            """
            {
              "ProcessId": 42,
              "ProcessId": 43,
              "StartedAtUtc": "2026-07-31T11:59:00+00:00",
              "HeartbeatAtUtc": "2026-07-31T12:00:00+00:00",
              "SchemaVersion": 3,
              "BrokerAssemblyPath": "C:/LocalAi.Broker.dll",
              "Compatibility": {
                "ProtocolVersion": 1,
                "BuildCompatibilityId": "localai-broker-v1"
              }
            }
            """,
            """
            {
              "ProcessId": 42,
              "StartedAtUtc": "2026-07-31T11:59:00+00:00",
              "HeartbeatAtUtc": "2026-07-31T12:00:00+00:00",
              "SchemaVersion": 3,
              "BrokerAssemblyPath": "C:/LocalAi.Broker.dll",
              "Compatibility": {
                "ProtocolVersion": 1,
                "BuildCompatibilityId": "localai-broker-v1"
              },
              "Unexpected": true
            }
            """,
            """
            {
              "StartedAtUtc": "2026-07-31T11:59:00+00:00",
              "HeartbeatAtUtc": "2026-07-31T12:00:00+00:00",
              "SchemaVersion": 3,
              "BrokerAssemblyPath": "C:/LocalAi.Broker.dll",
              "Compatibility": {
                "ProtocolVersion": 1,
                "BuildCompatibilityId": "localai-broker-v1"
              }
            }
            """,
            """
            {
              "ProcessId": 42,
              "StartedAtUtc": "2026-07-31T11:59:00+00:00",
              "HeartbeatAtUtc": "2026-07-31T12:00:00+00:00",
              "SchemaVersion": 3,
              "BrokerAssemblyPath": "C:/LocalAi.Broker.dll",
              "Compatibility": {
                "BuildCompatibilityId": "localai-broker-v1"
              }
            }
            """
        };

    [Theory]
    [MemberData(nameof(MalformedStateDocuments))]
    public async Task Malformed_state_is_absent_and_allows_replacement(string document)
    {
        using var fixture = new HostStateFixture(document);

        Assert.Null(BrokerProcess.ReadState(fixture.Root));

        var starts = 0;
        var readyState = new BrokerProcessState(
            99,
            Now,
            Now,
            BrokerCompatibilityContract.HostStateSchemaVersion,
            BrokerAssemblyPath,
            BrokerCompatibilityContract.Current);
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            fixture.Root,
            BrokerProcess.ReadState,
            state => state.ProcessId == 99,
            (_, _) =>
            {
                starts++;
                File.WriteAllText(
                    BrokerProcess.StatePath(fixture.Root),
                    JsonSerializer.Serialize(readyState, LocalAiJson.Strict));
                return 99;
            },
            new ManualTimeProvider(Now),
            static (_, _) => Task.CompletedTask);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Schema_two_state_without_compatibility_is_classified_as_live_legacy()
    {
        using var fixture = new HostStateFixture(
            """
            {
              "ProcessId": 42,
              "StartedAtUtc": "2026-07-31T11:59:00+00:00",
              "HeartbeatAtUtc": "2026-07-31T12:00:00+00:00",
              "SchemaVersion": 2,
              "BrokerAssemblyPath": "C:/LocalAi.Broker.dll"
            }
            """);
        var starts = 0;

        var state = BrokerProcess.ReadState(fixture.Root);

        Assert.NotNull(state);
        Assert.Null(state.Compatibility);
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            fixture.Root,
            BrokerProcess.ReadState,
            _ => true,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            new ManualTimeProvider(Now),
            static (_, _) => Task.CompletedTask);
        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal("broker_incompatible", exception.Code);
        Assert.Equal(0, starts);
    }

    private sealed class HostStateFixture : IDisposable
    {
        public HostStateFixture(string document)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "localai-process-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(BrokerProcess.StatePath(Root), document);
        }

        public string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
