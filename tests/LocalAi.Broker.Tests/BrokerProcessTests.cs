using System.ComponentModel;
using System.Diagnostics;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerProcessTests
{
    private static readonly string BrokerAssemblyPath =
        Path.GetFullPath("LocalAi.Broker.dll");

    public static TheoryData<Exception> UnreadableStateExceptions =>
        new()
        {
            new IOException("The host state is being replaced."),
            new UnauthorizedAccessException("The host state is temporarily unavailable.")
        };

    [Fact]
    public void Broker_start_info_does_not_inherit_caller_stdio()
    {
        var startInfo = BrokerProcess.CreateStartInfo(
            "dotnet",
            "\"LocalAi.Broker.dll\" serve --runtime runtime");

        Assert.True(startInfo.UseShellExecute);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
    }

    [Fact]
    public async Task Healthy_matching_process_is_reused()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now,
                BrokerCompatibilityContract.HostStateSchemaVersion,
                BrokerAssemblyPath,
                BrokerCompatibilityContract.Current),
            state => state.ProcessId == 42 && state.StartedAtUtc == now.AddMinutes(-1),
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Compatible_broker_at_another_assembly_path_is_reused()
    {
        var now = DateTimeOffset.UtcNow;
        var developmentAssembly = Path.GetFullPath("development/LocalAi.Broker.dll");
        var installedAssembly = Path.GetFullPath("installed/v1/LocalAi.Broker.dll");
        var starts = 0;
        var process = new BrokerProcess(
            "dotnet",
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(
                    42,
                    now.AddMinutes(-1),
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    installedAssembly,
                    BrokerCompatibilityContract.Current)
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    developmentAssembly,
                    BrokerCompatibilityContract.Current),
            state => state.ProcessId is 42 or 99,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask,
            arguments: "\"" + developmentAssembly + "\" serve --runtime runtime",
            brokerAssemblyPath: developmentAssembly);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Incompatible_live_host_at_same_assembly_path_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;

        await AssertLiveIncompatibleStateIsRejected(
            new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now,
                BrokerCompatibilityContract.HostStateSchemaVersion,
                BrokerAssemblyPath,
                new BrokerCompatibility(2, "other")),
            "actual schema=3 protocol=2 build=other; broker path=" + BrokerAssemblyPath);
    }

    [Fact]
    public async Task Legacy_live_host_at_same_assembly_path_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;

        await AssertLiveIncompatibleStateIsRejected(
            new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now,
                2,
                BrokerAssemblyPath,
                Compatibility: null),
            "actual schema=2 protocol=missing build=missing; broker path=" + BrokerAssemblyPath);
    }

    [Fact]
    public async Task Incompatible_live_host_with_differently_cased_build_id_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;

        await AssertLiveIncompatibleStateIsRejected(
            new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now,
                BrokerCompatibilityContract.HostStateSchemaVersion,
                BrokerAssemblyPath,
                new BrokerCompatibility(
                    BrokerCompatibilityContract.ProtocolVersion,
                    BrokerCompatibilityContract.BuildCompatibilityId.ToUpperInvariant())),
            "actual schema=3 protocol=1 build=" +
            BrokerCompatibilityContract.BuildCompatibilityId.ToUpperInvariant() +
            "; broker path=" + BrokerAssemblyPath);
    }

    [Fact]
    public async Task Incompatible_live_host_diagnostic_normalizes_and_bounds_untrusted_values()
    {
        var now = DateTimeOffset.UtcNow;
        var oversizedPath = "path\u0002" + new string('p', 600);

        await AssertLiveIncompatibleStateIsRejected(
            new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now,
                BrokerCompatibilityContract.HostStateSchemaVersion,
                oversizedPath,
                new BrokerCompatibility(
                    BrokerCompatibilityContract.ProtocolVersion,
                    "other\r\nbuild")),
            "actual schema=3 protocol=1 build=other??build; broker path=path?" +
            new string('p', 507));
    }

    [Fact]
    public async Task Incompatible_live_host_diagnostic_does_not_split_an_astral_rune_at_the_limit()
    {
        var now = DateTimeOffset.UtcNow;

        await AssertLiveIncompatibleStateIsRejected(
            new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now,
                BrokerCompatibilityContract.HostStateSchemaVersion,
                BrokerAssemblyPath,
                new BrokerCompatibility(
                    BrokerCompatibilityContract.ProtocolVersion,
                    new string('b', 511) + "\U0001F600")),
            "actual schema=3 protocol=1 build=" + new string('b', 511) +
            "; broker path=" + BrokerAssemblyPath);
    }

    [Fact]
    public async Task Incompatible_live_host_diagnostic_replaces_unpaired_surrogates()
    {
        var now = DateTimeOffset.UtcNow;

        await AssertLiveIncompatibleStateIsRejected(
            new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now,
                BrokerCompatibilityContract.HostStateSchemaVersion,
                BrokerAssemblyPath,
                new BrokerCompatibility(
                    BrokerCompatibilityContract.ProtocolVersion,
                    "high\uD800low\uDC00")),
            "actual schema=3 protocol=1 build=high\uFFFDlow\uFFFD; broker path=" +
            BrokerAssemblyPath);
    }

    [Fact]
    public async Task Incompatible_live_host_diagnostic_replaces_line_and_format_separators()
    {
        var now = DateTimeOffset.UtcNow;

        await AssertLiveIncompatibleStateIsRejected(
            new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now,
                BrokerCompatibilityContract.HostStateSchemaVersion,
                "path\u2028\u2029\u202E\u2066tail",
                new BrokerCompatibility(
                    BrokerCompatibilityContract.ProtocolVersion,
                    "build\u2028\u2029\u202E\u2066id")),
            "actual schema=3 protocol=1 build=build????id; broker path=path????tail");
    }

    [Fact]
    public async Task Non_owner_process_is_replaced()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(
                    42,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    Path.GetFullPath("another-version/LocalAi.Broker.dll"),
                    BrokerCompatibilityContract.Current)
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current),
            state => state.ProcessId == 99,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Non_owner_incompatible_looking_state_is_replaced_before_compatibility_is_checked()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(
                    42,
                    now.AddMinutes(-1),
                    now,
                    2,
                    BrokerAssemblyPath,
                    Compatibility: null)
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current),
            state => state.ProcessId == 99,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Stale_legacy_state_is_replaced()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(42, now, now.AddMinutes(-1), 1, BrokerAssemblyPath)
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current),
            state => state.ProcessId is 42 or 99,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Stale_state_with_invalid_broker_assembly_path_is_replaced()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(
                    42,
                    now,
                    now.AddMinutes(-1),
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    "\0",
                    BrokerCompatibilityContract.Current)
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current),
            state => state.ProcessId is 42 or 99,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Stale_state_starts_once_and_waits_for_matching_ready_state()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = 0;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ =>
            {
                reads++;
                return reads < 3
                    ? null
                    : new BrokerProcessState(
                        99,
                        now,
                        now,
                        BrokerCompatibilityContract.HostStateSchemaVersion,
                        BrokerAssemblyPath,
                        BrokerCompatibilityContract.Current);
            },
            state => state.ProcessId == 99,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, token) => Task.Delay(1, token),
            startupTimeout: TimeSpan.FromSeconds(2));

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
        Assert.True(reads >= 3);
    }

    [Theory]
    [MemberData(nameof(UnreadableStateExceptions))]
    public async Task Unreadable_state_starts_replacement(Exception exception)
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var readyState = new BrokerProcessState(
            99,
            now,
            now,
            BrokerCompatibilityContract.HostStateSchemaVersion,
            BrokerAssemblyPath,
            BrokerCompatibilityContract.Current);
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ =>
            {
                if (starts == 0)
                {
                    throw exception;
                }

                return readyState;
            },
            state => state.ProcessId == 99,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask,
            startupTimeout: TimeSpan.FromSeconds(2));

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Stale_heartbeat_does_not_probe_reused_process()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var staleProbes = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(
                    42,
                    now.AddMinutes(-2),
                    now.AddMinutes(-1),
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current)
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current),
            state =>
            {
                if (state.ProcessId == 42)
                {
                    staleProbes++;
                    throw new Win32Exception(5, "Access is denied.");
                }

                return state.ProcessId == 99;
            },
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask,
            startupTimeout: TimeSpan.FromSeconds(2));

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, staleProbes);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Inaccessible_process_state_starts_replacement()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(
                    42,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current)
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current),
            state =>
            {
                if (state.ProcessId == 42)
                {
                    throw new Win32Exception(5, "Access is denied.");
                }

                return state.ProcessId == 99;
            },
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask,
            startupTimeout: TimeSpan.FromSeconds(2));

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Startup_timeout_is_bounded()
    {
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => null,
            _ => false,
            (_, _) => 99,
            TimeProvider.System,
            static (_, _) => Task.CompletedTask,
            startupTimeout: TimeSpan.Zero);

        await Assert.ThrowsAsync<TimeoutException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Child_exiting_zero_reuses_compatible_lock_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = 0;
        var starts = 0;
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => ++reads == 1
                ? null
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current),
            state => state.ProcessId == 99,
            (_, _) =>
            {
                starts++;
                return FakeStartAttempt.Exited(42, 0);
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Child_exiting_zero_rejects_incompatible_lock_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = 0;
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => ++reads == 1
                ? null
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    new BrokerCompatibility(2, "other")),
            state => state.ProcessId == 99,
            static (_, _) => FakeStartAttempt.Exited(42, 0),
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal("broker_incompatible", exception.Code);
    }

    [Fact]
    public async Task Child_nonzero_exit_fails_without_waiting_for_timeout()
    {
        var delays = 0;
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => null,
            _ => false,
            static (_, _) => FakeStartAttempt.Exited(42, 17),
            TimeProvider.System,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal("broker_start_failed", exception.Code);
        Assert.Contains("17", exception.Message);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task Concurrent_clients_start_only_one_process()
    {
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "localai-process-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var ready = false;
        var starts = 0;
        BrokerProcess CreateProcess() => new(
            BrokerAssemblyPath,
            runtimeRoot,
            _ => ready
                ? new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current)
                : null,
            state => state.ProcessId == 99,
            (_, _) =>
            {
                Interlocked.Increment(ref starts);
                return 99;
            },
            TimeProvider.System,
            static (_, token) => Task.Delay(5, token),
            startupTimeout: TimeSpan.FromSeconds(2));

        var first = CreateProcess().EnsureRunningAsync(
            TestContext.Current.CancellationToken);
        var second = CreateProcess().EnsureRunningAsync(
            TestContext.Current.CancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);
        ready = true;

        await Task.WhenAll(first, second);

        Assert.Equal(1, starts);
    }

    private static async Task AssertLiveIncompatibleStateIsRejected(
        BrokerProcessState liveState,
        string expectedActualDetail)
    {
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => liveState,
            _ => true,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal("broker_incompatible", exception.Code);
        Assert.Equal(
            "expected schema=3 protocol=1 build=localai-broker-v1; " +
            expectedActualDetail,
            exception.Message);
        Assert.Equal(0, starts);
    }

    private sealed class FakeStartAttempt(int processId, int? exitCode) : IBrokerStartAttempt
    {
        private readonly int? _exitCode = exitCode;

        public int ProcessId { get; } = processId;

        public static FakeStartAttempt Running(int processId) => new(processId, null);

        public static FakeStartAttempt Exited(int processId, int exitCode) => new(processId, exitCode);

        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = _exitCode.GetValueOrDefault();
            return _exitCode.HasValue;
        }

        public void Dispose()
        {
        }
    }
}
