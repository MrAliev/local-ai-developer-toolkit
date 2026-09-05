using LocalAi.Broker;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// A failed job used to record the type of its exception and nothing else, so the sentence that
/// explained it — Ollama's own, already bounded and already redacted by the transport — was
/// discarded at the queue, again at the client, and again at the diagnostics log. Forty embed
/// jobs failed that way in four days and none of them could be explained afterwards (#349).
/// </summary>
public sealed class AFailedJobKeepsItsReasonTests
{
    private const string Reason = "Ollama request failed with HTTP 400. Response: {\"error\":\"input length exceeds maximum\"}";

    [Fact]
    public async Task The_reason_a_job_failed_survives_the_queue()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var lease = await FailNextAsync(queue, "HttpRequestException", Reason);

        var diagnostic = await queue.GetDiagnosticAsync(lease.Request.JobId, TestContext.Current.CancellationToken);

        Assert.NotNull(diagnostic);
        Assert.Equal(LocalJobState.Failed, diagnostic.State);
        Assert.Equal("HttpRequestException", diagnostic.FailureCode);
        Assert.Equal(Reason, diagnostic.FailureReason);
    }

    /// <summary>
    /// The transport bounds its excerpt already; the queue bounds it again because it is a
    /// durable file and the caller of FailAsync is not always that transport.
    /// </summary>
    [Fact]
    public async Task A_reason_past_the_bound_is_cut_rather_than_stored_whole()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var lease = await FailNextAsync(
            queue,
            "InvalidDataException",
            new string('x', DurableQueue.MaximumFailureReasonCharacters * 3));

        var diagnostic = await queue.GetDiagnosticAsync(lease.Request.JobId, TestContext.Current.CancellationToken);

        Assert.NotNull(diagnostic);
        Assert.NotNull(diagnostic.FailureReason);
        Assert.Equal(DurableQueue.MaximumFailureReasonCharacters, diagnostic.FailureReason.Length);
    }

    /// <summary>
    /// Not every failure has a sentence — a job failed before any backend answered, or one that
    /// failed on a release that did not record one. The state has to read back either way.
    /// </summary>
    [Fact]
    public async Task A_failure_with_no_reason_reads_back_as_a_failure()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var lease = await FailNextAsync(queue, "BackendUnavailableException", failureReason: null);

        var diagnostic = await queue.GetDiagnosticAsync(lease.Request.JobId, TestContext.Current.CancellationToken);

        Assert.NotNull(diagnostic);
        Assert.Equal(LocalJobState.Failed, diagnostic.State);
        Assert.Null(diagnostic.FailureReason);
    }

    [Fact]
    public async Task The_client_hands_the_reason_to_whoever_awaited_the_job()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var client = new BrokerClient(
            queue,
            new AlreadyRunningBroker(),
            static (delay, token) => Task.Delay(TimeSpan.FromMilliseconds(5), token),
            TimeSpan.FromSeconds(30));
        var request = LocalJobRequestFactory.CreateEmbed(
            "embed:reason",
            LocalJobPriority.Background,
            "embed-model",
            ["chunk"]);

        var pending = client.ExecuteAsync<EmbedJobOutput>(
            request,
            TestContext.Current.CancellationToken);
        await FailNextAsync(queue, "HttpRequestException", Reason);

        var error = await Assert.ThrowsAsync<BrokerJobFailedException>(() => pending);

        Assert.Equal(Reason, error.FailureReason);
        Assert.Contains("input length exceeds maximum", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The broker's own plumbing failures stay type-only, as they have been since this log was
    /// written: their messages carry filesystem paths, not explanations. What the job itself
    /// failed with is the exception, and it is the whole point of the log.
    /// </summary>
    [Fact]
    public async Task The_broker_reports_what_the_job_failed_with()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        await queue.EnqueueAsync(
            LocalJobRequestFactory.CreateEmbed(
                "embed:reported",
                LocalJobPriority.Background,
                "embed-model",
                ["chunk"]),
            TestContext.Current.CancellationToken);
        var diagnostics = new List<BrokerHostDiagnostic>();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var host = new BrokerHost(
            queue,
            "reporting-worker",
            (_, _, _) => throw new HttpRequestException(Reason),
            idleDelay: (_, _) =>
            {
                stop.Cancel();
                return Task.CompletedTask;
            },
            diagnostic: diagnostics.Add);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(stop.Token));

        var reported = Assert.Single(diagnostics);
        Assert.Equal("execute", reported.Operation);
        Assert.Equal(nameof(HttpRequestException), reported.ExceptionType);
        Assert.Equal(Reason, reported.Reason);
    }

    private static async Task<LeasedJob> FailNextAsync(
        DurableQueue queue,
        string failureCode,
        string? failureReason)
    {
        var lease = await queue.LeaseNextAsync("test-worker", TestContext.Current.CancellationToken);
        if (lease is null)
        {
            await queue.EnqueueAsync(
                LocalJobRequestFactory.CreateEmbed(
                    "embed:" + Guid.NewGuid().ToString("N"),
                    LocalJobPriority.Background,
                    "embed-model",
                    ["chunk"]),
                TestContext.Current.CancellationToken);
            lease = await queue.LeaseNextAsync("test-worker", TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Expected queued work.");
        }

        await queue.FailAsync(
            lease.Request.JobId,
            lease.WorkerId,
            lease.LeaseId,
            failureCode,
            failureReason,
            TestContext.Current.CancellationToken);
        return lease;
    }

    private sealed class AlreadyRunningBroker : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
