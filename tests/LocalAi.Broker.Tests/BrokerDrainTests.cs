using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;

#pragma warning disable xUnit1051

namespace LocalAi.Broker.Tests;

public sealed class BrokerDrainTests
{
    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateEmbed(
            key,
            LocalJobPriority.Foreground,
            "test-model",
            ["input"]);

    /// <summary>
    /// Draining is not cancelling. A stop that has to abandon a running inference in order to
    /// replace a binary is the reason this exists at all, so the job in flight must finish and
    /// be reported before the loop gives up its turn.
    /// </summary>
    [Fact]
    public async Task Draining_finishes_the_running_job_and_leases_nothing_further()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var first = await queue.EnqueueAsync(Request("first"));
        await queue.EnqueueAsync(Request("second"));
        var drain = false;
        var executed = new List<Guid>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        async Task<BrokerExecutionResult> Execute(
            LocalJobRequest request,
            CancellationToken cancellationToken)
        {
            // The drain arrives while this job is running: the job that started before it is
            // the one that must not be lost.
            drain = true;
            await Task.Delay(30, cancellationToken);
            executed.Add(request.JobId);
            return new BrokerExecutionResult(
                JsonSerializer.SerializeToElement(new { request.JobId }));
        }

        var host = new BrokerHost(
            queue,
            "drain-worker",
            Execute,
            idleDelay: static (delay, token) => Task.Delay(delay, token),
            idleInterval: TimeSpan.FromMilliseconds(5),
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        // Returns rather than throws: this is an orderly exit, not a cancellation.
        await host.RunAsync(timeout.Token, () => drain);

        Assert.Equal([first.JobId], executed);
        // The second job is untouched and waits for whoever runs next.
        Assert.Single(await queue.ListQueuedAsync());
    }

    [Fact]
    public async Task A_drain_requested_before_any_work_takes_none()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        await queue.EnqueueAsync(Request("untouched"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var host = new BrokerHost(
            queue,
            "drain-worker",
            (_, _) => throw new InvalidOperationException("Nothing may be leased while draining."),
            idleDelay: static (delay, token) => Task.Delay(delay, token),
            idleInterval: TimeSpan.FromMilliseconds(5),
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        await host.RunAsync(timeout.Token, static () => true);

        Assert.Single(await queue.ListQueuedAsync());
    }

    [Fact]
    public void A_shutdown_request_names_the_broker_it_is_meant_for()
    {
        using var root = new TemporaryRuntimeRoot();
        var startedAt = new DateTimeOffset(2026, 8, 2, 3, 0, 0, TimeSpan.Zero);

        BrokerShutdownRequestStore.Write(root.Path, new BrokerShutdownRequest(4242, startedAt));
        var request = BrokerShutdownRequestStore.Read(root.Path);

        Assert.Equal(4242, request?.ProcessId);
        // The start time is what stops a stale request from shutting down a healthy broker that
        // inherited the process id.
        Assert.Equal(startedAt, request?.StartedAtUtc);

        BrokerShutdownRequestStore.Delete(root.Path);
        Assert.Null(BrokerShutdownRequestStore.Read(root.Path));
    }

    [Fact]
    public void A_malformed_shutdown_request_is_not_a_shutdown()
    {
        using var root = new TemporaryRuntimeRoot();
        File.WriteAllText(BrokerShutdownRequestStore.PathFor(root.Path), "{ not json");

        // Exiting on an unreadable file would make one corrupt byte enough to stop the broker.
        Assert.Null(BrokerShutdownRequestStore.Read(root.Path));
    }
}
