using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerProcessTests
{
    [Fact]
    public async Task Healthy_matching_process_is_reused()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        var process = new BrokerProcess(
            "broker.exe",
            "runtime",
            _ => new BrokerProcessState(42, now.AddMinutes(-1), now, 1),
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
    public async Task Stale_state_starts_once_and_waits_for_matching_ready_state()
    {
        var now = DateTimeOffset.UtcNow;
        var reads = 0;
        var starts = 0;
        var process = new BrokerProcess(
            "broker.exe",
            "runtime",
            _ =>
            {
                reads++;
                return reads < 3
                    ? null
                    : new BrokerProcessState(99, now, now, 1);
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
    public async Task Startup_timeout_is_bounded()
    {
        var process = new BrokerProcess(
            "broker.exe",
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
            "broker.exe",
            runtimeRoot,
            _ => ready ? new BrokerProcessState(99, now, now, 1) : null,
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
