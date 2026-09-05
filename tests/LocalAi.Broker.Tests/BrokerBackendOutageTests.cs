using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Ollama being down is a normal, transient condition: a boot race, a service restart, a user
/// who closed the tray app. The broker has to outlive it.
///
/// It did not. Scheduling reaches the backend to decide which queued job to lease, and nothing
/// on that path caught anything: the transport gives up after its own retries and the
/// HttpRequestException travelled out of the scheduling callback, out of LeaseNextAsync, out of
/// RunAsync and off the top of the process. On a machine where the MCP servers start before
/// Ollama's autostart finishes, that is a Windows crash dialog on every boot.
/// </summary>
public sealed class BrokerBackendOutageTests
{
    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateEmbed(
            key,
            LocalJobPriority.Foreground,
            "test-model",
            ["input"]);

    /// <summary>
    /// The connection is refused for the first three scheduling turns and then Ollama finishes
    /// coming up, which is the boot race as the machine actually plays it out. The host has to
    /// still be running to notice.
    /// </summary>
    [Fact]
    public async Task An_unreachable_backend_during_scheduling_does_not_stop_the_host()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 28, 19, 52, 0, TimeSpan.Zero));
        var queue = new DurableQueue(root.Path, clock);
        var request = Request("boot-race");
        await queue.EnqueueAsync(request, TestContext.Current.CancellationToken);
        var resolver = new ScheduleMetadataResolver(
            ModelRoutingCatalog.LoadEmbedded(),
            new DurationEstimator());

        var scheduleCalls = 0;
        var executed = new List<Guid>();
        var drain = false;
        var diagnostics = new List<BrokerHostDiagnostic>();

        Task<IReadOnlyList<ScheduledJobCandidate>> Schedule(
            IReadOnlyList<QueuedJobCandidate> candidates,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref scheduleCalls) <= 3)
            {
                throw new HttpRequestException(
                    "No connection could be made because the target machine actively " +
                    "refused it. (127.0.0.1:11434)");
            }

            return Task.FromResult<IReadOnlyList<ScheduledJobCandidate>>(
                candidates.Select(candidate => resolver.Resolve(candidate)).ToArray());
        }

        Task<BrokerExecutionResult> Execute(
            LocalJobRequest job,
            IJobProgress progress,
            CancellationToken cancellationToken)
        {
            executed.Add(job.JobId);
            drain = true;
            return Task.FromResult(
                new BrokerExecutionResult(JsonSerializer.SerializeToElement(new { job.JobId })));
        }

        var host = new BrokerHost(
            queue,
            "boot-race-worker",
            Execute,
            clock,
            idleDelay: (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            idleInterval: TimeSpan.FromMilliseconds(5),
            scheduler: new ModelAwareScheduler(clock),
            scheduleMetadata: Schedule,
            diagnostic: diagnostics.Add);

        // Returns rather than throws: an unreachable backend is a state to wait out, not a fault
        // to die of. There is no deadline here on purpose -- if the host stops waiting, the run
        // timeout CI applies to the whole solution is what says so.
        await host.RunAsync(TestContext.Current.CancellationToken, () => drain);

        Assert.Equal([request.JobId], executed);
        // Three refusals reported and the job still ran. How many scheduling turns it took after
        // the backend answered belongs to the scheduler, so it is not asserted here.
        Assert.True(scheduleCalls > 3, $"The host stopped scheduling after {scheduleCalls} turns.");
        Assert.Equal(3, diagnostics.Count);
        Assert.All(
            diagnostics,
            diagnostic =>
            {
                Assert.Equal("schedule", diagnostic.Operation);
                Assert.Equal(nameof(HttpRequestException), diagnostic.ExceptionType);
                Assert.Equal("boot-race-worker", diagnostic.WorkerId);
            });
    }

    /// <summary>
    /// The queued job is still there afterwards. Surviving the outage is worth nothing if the
    /// work it was holding was dropped on the way through.
    /// </summary>
    [Fact]
    public async Task Work_queued_during_an_outage_is_kept_until_the_backend_answers()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 28, 19, 52, 0, TimeSpan.Zero));
        var queue = new DurableQueue(root.Path, clock);
        await queue.EnqueueAsync(Request("waits"), TestContext.Current.CancellationToken);

        var refusals = 0;
        var drain = false;

        Task<IReadOnlyList<ScheduledJobCandidate>> Schedule(
            IReadOnlyList<QueuedJobCandidate> candidates,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref refusals) >= 3)
            {
                drain = true;
            }

            throw new HttpRequestException("Connection refused. (127.0.0.1:11434)");
        }

        var host = new BrokerHost(
            queue,
            "outage-worker",
            (_, _, _) => throw new UnreachableException(),
            clock,
            idleDelay: (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            idleInterval: TimeSpan.FromMilliseconds(5),
            scheduler: new ModelAwareScheduler(clock),
            scheduleMetadata: Schedule);

        await host.RunAsync(TestContext.Current.CancellationToken, () => drain);

        Assert.Single(await queue.ListQueuedAsync(TestContext.Current.CancellationToken));
    }
}
