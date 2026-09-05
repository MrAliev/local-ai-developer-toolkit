using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// A model pull is gigabytes and minutes, and the process doing it is not the process waiting on
/// it. What the worker learns has to reach the client somehow, and the only channel between them
/// is the job's own directory.
///
/// Written as a sibling file rather than a field on the state document, exactly as a failure
/// reason is: the state document is read with <c>UnmappedMemberHandling.Disallow</c>, so a new
/// field would make every job written by this release unreadable to the release before it — and
/// an upgrade leaves an old broker running against a new console for as long as it takes to
/// restart. A file the old code never opens costs it nothing.
/// </summary>
public sealed class ADownloadSaysHowFarItIsTests
{
    [Fact]
    public async Task A_running_job_can_say_how_far_it_has_got()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var lease = await LeaseNextAsync(queue);

        await queue.ReportProgressAsync(
            lease.Request.JobId,
            lease.WorkerId,
            lease.LeaseId,
            new JobProgress("downloading", null, 3_221_225_472, 9_663_676_416),
            TestContext.Current.CancellationToken);

        var diagnostic = await queue.GetDiagnosticAsync(
            lease.Request.JobId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(diagnostic?.Progress);
        Assert.Equal("downloading", diagnostic.Progress.Phase, StringComparer.Ordinal);
        Assert.Equal(3_221_225_472, diagnostic.Progress.Completed);
        Assert.Equal(9_663_676_416, diagnostic.Progress.Total);
    }

    /// <summary>
    /// Written many times over one download, so it is a current position and not a log. The last
    /// report is what a reader has to see — an appended history would grow without bound in a
    /// directory that is copied to the archive when the job ends.
    /// </summary>
    [Fact]
    public async Task The_latest_report_is_the_one_that_reads_back()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var lease = await LeaseNextAsync(queue);

        foreach (var completed in (long[])[1_000_000, 2_000_000, 3_000_000])
        {
            await queue.ReportProgressAsync(
                lease.Request.JobId,
                lease.WorkerId,
                lease.LeaseId,
                new JobProgress("downloading", null, completed, 9_000_000),
                TestContext.Current.CancellationToken);
        }

        var diagnostic = await queue.GetDiagnosticAsync(
            lease.Request.JobId,
            TestContext.Current.CancellationToken);

        Assert.Equal(3_000_000, diagnostic!.Progress!.Completed);
    }

    /// <summary>
    /// Guarded like every other write a worker makes. A report is not dangerous, but a queue that
    /// accepts one from a lease it does not hold accepts a stale worker's position as the current
    /// one, and the reader has no way to tell.
    /// </summary>
    [Fact]
    public async Task A_report_from_a_lease_the_worker_does_not_hold_is_refused()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var lease = await LeaseNextAsync(queue);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            queue.ReportProgressAsync(
                lease.Request.JobId,
                lease.WorkerId,
                Guid.NewGuid(),
                new JobProgress("downloading", null, 1, 2),
                TestContext.Current.CancellationToken));
    }

    /// <summary>A job nobody has reported on reads back with no position, not with a zero.</summary>
    [Fact]
    public async Task A_job_with_no_report_has_no_position()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var lease = await LeaseNextAsync(queue);

        var diagnostic = await queue.GetDiagnosticAsync(
            lease.Request.JobId,
            TestContext.Current.CancellationToken);

        Assert.Null(diagnostic!.Progress);
    }

    /// <summary>
    /// The worker and the reader are different processes and the job's directory is the only
    /// channel between them. A position that only appears once the job has finished is a
    /// slower way of saying nothing, so this asserts a reader sees it while the job runs.
    /// </summary>
    [Fact]
    public async Task What_a_running_job_reports_reaches_a_reader_before_it_finishes()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var queued = await queue.EnqueueAsync(
            LocalJobRequestFactory.CreateModelMaintenance(
                "local-lm:model-pull:signed-7:qwen3.5:9b",
                LocalJobPriority.Background,
                ModelMaintenanceOperation.Pull,
                "qwen3.5:9b",
                "signed-7"),
            TestContext.Current.CancellationToken);

        var reported = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var host = new BrokerHost(
            queue,
            "download-worker",
            async (_, progress, token) =>
            {
                await progress.ReportAsync(
                    new JobProgress("downloading", null, 512, 1024),
                    token);
                reported.SetResult();
                await finish.Task;
                return new BrokerExecutionResult(
                    JsonSerializer.SerializeToElement(
                        new ModelMaintenanceJobOutput("success"),
                        LocalAiJson.Strict));
            },
            // The host's own pacing, as every other test here passes it. A zero delay makes
            // the idle loop spin without yielding, which starves the continuation that ends it.
            idleDelay: static (delay, token) => Task.Delay(delay, token),
            idleInterval: TimeSpan.FromMilliseconds(5));

        var running = host.RunAsync(stop.Token);
        await reported.Task.WaitAsync(TestContext.Current.CancellationToken);

        var diagnostic = await queue.GetDiagnosticAsync(
            queued.JobId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(diagnostic?.Progress);
        Assert.Equal(512, diagnostic.Progress.Completed);
        Assert.Equal(1024, diagnostic.Progress.Total);

        finish.SetResult();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    private static async Task<LeasedJob> LeaseNextAsync(DurableQueue queue)
    {
        await queue.EnqueueAsync(
            LocalJobRequestFactory.CreateModelMaintenance(
                "local-lm:model-pull:signed-7:qwen3.5:9b",
                LocalJobPriority.Background,
                ModelMaintenanceOperation.Pull,
                "qwen3.5:9b",
                "signed-7"),
            TestContext.Current.CancellationToken);

        return await queue.LeaseNextAsync(
                "download-worker",
                cancellationToken: TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Expected queued work.");
    }
}
