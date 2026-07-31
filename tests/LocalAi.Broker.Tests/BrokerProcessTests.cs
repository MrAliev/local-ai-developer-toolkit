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
    public async Task Default_poll_delay_uses_injected_time_provider()
    {
        var timeProvider = new RecordingTimerTimeProvider();

        await BrokerProcess.DelayAsync(
            TimeSpan.FromMilliseconds(1),
            timeProvider,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, timeProvider.CreateTimerCallCount);
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

    [Theory]
    [InlineData(-5)]
    [InlineData(5)]
    public async Task Heartbeat_at_clock_skew_boundary_is_reused(int offsetSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => new BrokerProcessState(
                42,
                now.AddMinutes(-1),
                now.AddSeconds(offsetSeconds),
                BrokerCompatibilityContract.HostStateSchemaVersion,
                BrokerAssemblyPath,
                BrokerCompatibilityContract.Current),
            state => state.ProcessId == 42,
            (_, _) =>
            {
                starts++;
                return 99;
            },
            new ManualTimeProvider(now),
            static (_, _) => Task.CompletedTask);

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Heartbeat_beyond_future_clock_skew_is_replaced_without_process_probe()
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
                    now.AddMinutes(-1),
                    now.AddSeconds(5).AddTicks(1),
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
                }

                return state.ProcessId == 99;
            },
            (_, _) =>
            {
                starts++;
                return 99;
            },
            new ManualTimeProvider(now),
            static (_, _) => Task.CompletedTask);

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
        var startAttempt = FakeStartAttempt.Running(99);
        var starts = 0;
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => null,
            _ => false,
            (_, _) =>
            {
                starts++;
                return startAttempt;
            },
            TimeProvider.System,
            static (_, _) => Task.CompletedTask,
            startupTimeout: TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal("broker_start_timeout", exception.Code);
        Assert.Contains("last observation:", exception.Message);
        Assert.Contains("host state is absent or unreadable", exception.Message);
        Assert.Equal(0, starts);
        Assert.False(startAttempt.IsDisposed);
    }

    [Fact]
    public async Task Startup_budget_expiring_during_initial_state_read_does_not_start()
    {
        var clock = new RollingBackTimeProvider(DateTimeOffset.UtcNow);
        var starts = 0;
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(101), TimeSpan.Zero);
                return null;
            },
            _ => false,
            (_, _) =>
            {
                starts++;
                return FakeStartAttempt.Running(99);
            },
            clock,
            static (_, _) => Task.CompletedTask,
            startupTimeout: TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal("broker_start_timeout", exception.Code);
        Assert.Contains("phase: initial state observation", exception.Message);
        Assert.Contains("host state is absent or unreadable", exception.Message);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Startup_timeout_includes_wait_for_startup_semaphore()
    {
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "localai-process-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        using var startEntered = new ManualResetEventSlim();
        using var releaseStart = new ManualResetEventSlim();
        var ready = false;
        var secondStarts = 0;
        BrokerProcessState? ReadState(string _) =>
            ready
                ? new BrokerProcessState(
                    42,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current)
                : null;
        var first = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            runtimeRoot,
            ReadState,
            state => state.ProcessId == 42,
            (_, _) =>
            {
                startEntered.Set();
                releaseStart.Wait(TestContext.Current.CancellationToken);
                ready = true;
                return FakeStartAttempt.Running(42);
            },
            TimeProvider.System,
            static (_, token) => Task.Delay(5, token),
            startupTimeout: TimeSpan.FromSeconds(5));
        var second = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            runtimeRoot,
            ReadState,
            state => state.ProcessId == 42,
            (_, _) =>
            {
                Interlocked.Increment(ref secondStarts);
                return FakeStartAttempt.Running(99);
            },
            TimeProvider.System,
            static (_, token) => Task.Delay(5, token),
            startupTimeout: TimeSpan.FromMilliseconds(75));

        var firstTask = first.EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.True(
            startEntered.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        using var safetyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        safetyCancellation.CancelAfter(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
                () => second.EnsureRunningAsync(safetyCancellation.Token));

            Assert.Equal("broker_start_timeout", exception.Code);
            Assert.Contains("startup semaphore contention", exception.Message);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(0, secondStarts);
        }
        finally
        {
            releaseStart.Set();
            await firstTask;
        }
    }

    [Fact]
    public async Task Startup_semaphore_wait_cancellation_is_prompt_and_semaphore_remains_reusable()
    {
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "localai-process-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        using var startEntered = new ManualResetEventSlim();
        using var releaseStart = new ManualResetEventSlim();
        var ready = false;
        var blockedStarts = 0;
        var reuseStarts = 0;
        BrokerProcessState? ReadState(string _) =>
            ready
                ? new BrokerProcessState(
                    42,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    BrokerCompatibilityContract.Current)
                : null;
        var first = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            runtimeRoot,
            ReadState,
            state => state.ProcessId == 42,
            (_, _) =>
            {
                startEntered.Set();
                releaseStart.Wait(TestContext.Current.CancellationToken);
                ready = true;
                return FakeStartAttempt.Running(42);
            },
            TimeProvider.System,
            static (_, token) => Task.Delay(5, token),
            startupTimeout: TimeSpan.FromSeconds(5));
        var blocked = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            runtimeRoot,
            ReadState,
            state => state.ProcessId == 42,
            (_, _) =>
            {
                blockedStarts++;
                return FakeStartAttempt.Running(99);
            },
            TimeProvider.System,
            static (_, token) => Task.Delay(5, token),
            startupTimeout: TimeSpan.FromSeconds(5));

        var firstTask = first.EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.True(
            startEntered.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var blockedTask = blocked.EnsureRunningAsync(cancellation.Token);
        await Task.Delay(
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => blockedTask);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(0, blockedStarts);
        }
        finally
        {
            releaseStart.Set();
            await firstTask;
        }

        var reuse = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            runtimeRoot,
            ReadState,
            state => state.ProcessId == 42,
            (_, _) =>
            {
                reuseStarts++;
                return FakeStartAttempt.Running(100);
            },
            TimeProvider.System,
            static (_, token) => Task.Delay(5, token),
            startupTimeout: TimeSpan.FromSeconds(1));

        await reuse.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, reuseStarts);
    }

    [Fact]
    public async Task Startup_timeout_uses_monotonic_time_when_utc_clock_rolls_back()
    {
        var clock = new RollingBackTimeProvider(DateTimeOffset.UtcNow);
        var delays = 0;
        using var safetyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        safetyCancellation.CancelAfter(TimeSpan.FromSeconds(2));
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => null,
            _ => false,
            (_, _) => FakeStartAttempt.Running(99),
            clock,
            (_, _) =>
            {
                delays++;
                clock.Advance(
                    TimeSpan.FromMilliseconds(60),
                    TimeSpan.FromMinutes(-1));
                if (delays > 10)
                {
                    safetyCancellation.Cancel();
                }

                return Task.CompletedTask;
            },
            startupTimeout: TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(safetyCancellation.Token));

        Assert.Equal("broker_start_timeout", exception.Code);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task Child_exiting_zero_lock_owner_did_not_publish_times_out_with_diagnostics()
    {
        var startAttempt = FakeStartAttempt.Exited(42, 0);
        var clock = new RollingBackTimeProvider(DateTimeOffset.UtcNow);
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => null,
            _ => false,
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(2), TimeSpan.Zero);
                return startAttempt;
            },
            clock,
            static (_, _) => Task.CompletedTask,
            startupTimeout: TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal("broker_start_timeout", exception.Code);
        Assert.Contains(
            "startup process 42 exited successfully; lock owner did not publish compatible state",
            exception.Message);
        Assert.True(startAttempt.IsDisposed);
    }

    [Fact]
    public async Task Startup_cancellation_propagates_and_disposes_attempt()
    {
        using var cancellation = new CancellationTokenSource();
        var startAttempt = FakeStartAttempt.Running(42);
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => null,
            _ => false,
            (_, _) => startAttempt,
            TimeProvider.System,
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => process.EnsureRunningAsync(cancellation.Token));

        Assert.True(startAttempt.IsDisposed);
    }

    [Fact]
    public async Task Child_exiting_zero_reuses_compatible_lock_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = 0;
        var starts = 0;
        var delays = 0;
        var startAttempt = FakeStartAttempt.Exited(42, 0);
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => ++reads < 3
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
                return startAttempt;
            },
            TimeProvider.System,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        await process.EnsureRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, starts);
        Assert.True(startAttempt.TryGetExitCodeCallCount > 0);
        Assert.True(startAttempt.IsDisposed);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task Child_exiting_zero_rejects_incompatible_lock_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = 0;
        var delays = 0;
        var startAttempt = FakeStartAttempt.Exited(42, 0);
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => ++reads < 3
                ? null
                : new BrokerProcessState(
                    99,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    BrokerAssemblyPath,
                    new BrokerCompatibility(2, "other")),
            state => state.ProcessId == 99,
            (_, _) => startAttempt,
            TimeProvider.System,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<BrokerBootstrapException>(
            () => process.EnsureRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal("broker_incompatible", exception.Code);
        Assert.True(startAttempt.TryGetExitCodeCallCount > 0);
        Assert.True(startAttempt.IsDisposed);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task Child_nonzero_exit_fails_without_waiting_for_timeout()
    {
        var delays = 0;
        var startAttempt = FakeStartAttempt.Exited(42, 17);
        var process = BrokerProcess.CreateForTesting(
            BrokerAssemblyPath,
            "runtime",
            _ => null,
            _ => false,
            (_, _) => startAttempt,
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
        Assert.True(startAttempt.TryGetExitCodeCallCount > 0);
        Assert.True(startAttempt.IsDisposed);
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

        public int TryGetExitCodeCallCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public static FakeStartAttempt Running(int processId) => new(processId, null);

        public static FakeStartAttempt Exited(int processId, int exitCode) => new(processId, exitCode);

        public bool TryGetExitCode(out int exitCode)
        {
            TryGetExitCodeCallCount++;
            exitCode = _exitCode.GetValueOrDefault();
            return _exitCode.HasValue;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RollingBackTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed, TimeSpan utcChange)
        {
            _timestamp += elapsed.Ticks;
            _utcNow += utcChange;
        }
    }

    private sealed class RecordingTimerTimeProvider : TimeProvider
    {
        public int CreateTimerCallCount { get; private set; }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            CreateTimerCallCount++;
            return TimeProvider.System.CreateTimer(
                callback,
                state,
                dueTime,
                period);
        }
    }
}
