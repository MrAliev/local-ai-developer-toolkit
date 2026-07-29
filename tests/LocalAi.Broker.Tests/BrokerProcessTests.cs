using System.ComponentModel;
using System.Diagnostics;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerProcessTests
{
    private static readonly string BrokerAssemblyPath =
        Path.GetFullPath("LocalAi.Broker.dll");

    [Fact]
    public void Broker_start_info_does_not_inherit_caller_stdio()
    {
        var startInfo = BrokerProcess.CreateStartInfo(
            "dotnet",
            "\"LocalAi.Broker.dll\" serve --runtime runtime");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
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
                2,
                BrokerAssemblyPath),
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
    public async Task Matching_process_with_another_broker_assembly_is_replaced()
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
                    2,
                    Path.GetFullPath("another-version/LocalAi.Broker.dll"))
                : new BrokerProcessState(99, now, now, 2, BrokerAssemblyPath),
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
    public async Task Schema_one_state_is_replaced()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(42, now, now, 1, BrokerAssemblyPath)
                : new BrokerProcessState(99, now, now, 2, BrokerAssemblyPath),
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
    public async Task Invalid_broker_assembly_path_is_replaced()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            BrokerAssemblyPath,
            "runtime",
            _ => starts == 0
                ? new BrokerProcessState(42, now, now, 2, "\0")
                : new BrokerProcessState(99, now, now, 2, BrokerAssemblyPath),
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
                    : new BrokerProcessState(99, now, now, 2, BrokerAssemblyPath);
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
                    2,
                    BrokerAssemblyPath)
                : new BrokerProcessState(99, now, now, 2, BrokerAssemblyPath),
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
                ? new BrokerProcessState(42, now, now, 2, BrokerAssemblyPath)
                : new BrokerProcessState(99, now, now, 2, BrokerAssemblyPath),
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
                ? new BrokerProcessState(99, now, now, 2, BrokerAssemblyPath)
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
}
