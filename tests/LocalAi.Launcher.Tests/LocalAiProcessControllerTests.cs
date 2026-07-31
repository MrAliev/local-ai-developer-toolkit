namespace LocalAi.Launcher.Tests;

public sealed class LocalAiProcessControllerTests
{
    [Fact]
    public void Fresh_schema_three_broker_state_returns_exact_ownership()
    {
        using var install = TestInstall.CreateComplete("v1");
        var now = new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
        var started = now.AddMinutes(-1);
        var assemblyPath = Path.Combine(
            install.VersionDirectory("v1"),
            "LocalAi.Broker.dll");
        WriteHostState(
            install.Root,
            42,
            started,
            now.AddSeconds(-5),
            3,
            assemblyPath,
            new CompatibilityForTest(1, "localai-broker-v1"));

        var ownership = new BrokerHostStateReader(new FixedTimeProvider(now))
            .ReadFreshOwnership(install.Root);

        Assert.NotNull(ownership);
        Assert.Equal(42, ownership.ProcessId);
        Assert.Equal(started, ownership.StartedAtUtc);
        Assert.Equal(assemblyPath, ownership.BrokerAssemblyPath);
    }

    [Fact]
    public void Fresh_schema_two_broker_state_without_compatibility_returns_ownership()
    {
        using var install = TestInstall.CreateComplete("v1");
        var now = new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
        var started = now.AddMinutes(-1);
        var assemblyPath = Path.Combine(
            install.VersionDirectory("v1"),
            "LocalAi.Broker.dll");
        WriteHostState(
            install.Root,
            42,
            started,
            now,
            2,
            assemblyPath,
            compatibility: null);

        var ownership = new BrokerHostStateReader(new FixedTimeProvider(now))
            .ReadFreshOwnership(install.Root);

        Assert.NotNull(ownership);
        Assert.Equal(42, ownership.ProcessId);
        Assert.Equal(started, ownership.StartedAtUtc);
        Assert.Equal(assemblyPath, ownership.BrokerAssemblyPath);
    }

    [Theory]
    [InlineData(1, "2026-07-31T10:00:00.0000000+00:00", "path", true)]
    [InlineData(3, "2026-07-31T09:59:54.9999999+00:00", "path", true)]
    [InlineData(2, "2026-07-31T10:00:00.0000000+00:00", "", false)]
    [InlineData(3, "2026-07-31T10:00:00.0000000+00:00", "path", false)]
    public void Invalid_broker_states_return_no_ownership(
        int schemaVersion,
        string heartbeatAtUtc,
        string assemblyPath,
        bool includeCompatibility)
    {
        using var install = TestInstall.CreateComplete("v1");
        var now = new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
        WriteHostState(
            install.Root,
            42,
            new DateTimeOffset(2026, 7, 31, 9, 59, 0, TimeSpan.Zero),
            DateTimeOffset.Parse(heartbeatAtUtc),
            schemaVersion,
            assemblyPath,
            includeCompatibility
                ? new CompatibilityForTest(1, "localai-broker-v1")
                : null);

        var ownership = new BrokerHostStateReader(new FixedTimeProvider(now))
            .ReadFreshOwnership(install.Root);

        Assert.Null(ownership);
    }

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
                null),
            new ProcessSnapshot(
                15,
                started,
                Environment.GetEnvironmentVariable("ComSpec")!,
                Path.Combine(v2, "LocalAi.Broker.dll"))
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

    private static void WriteHostState(
        string runtimeRoot,
        int processId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset heartbeatAtUtc,
        int schemaVersion,
        string brokerAssemblyPath,
        CompatibilityForTest? compatibility) =>
        File.WriteAllText(
            Path.Combine(runtimeRoot, "host.json"),
            System.Text.Json.JsonSerializer.Serialize(
                new HostStateForTest(
                    processId,
                    startedAtUtc,
                    heartbeatAtUtc,
                    schemaVersion,
                    brokerAssemblyPath,
                    compatibility)));

    private sealed record HostStateForTest(
        int ProcessId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset HeartbeatAtUtc,
        int SchemaVersion,
        string BrokerAssemblyPath,
        CompatibilityForTest? Compatibility);

    private sealed record CompatibilityForTest(
        int ProtocolVersion,
        string BuildCompatibilityId);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
