using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerClientTests
{
    [Fact]
    public async Task Embed_enqueues_starts_host_and_returns_typed_output()
    {
        using var fixture = new BrokerClientFixture();
        var request = LocalJobRequestFactory.CreateEmbed(
            "embed:test",
            LocalJobPriority.Interactive,
            "embed-model",
            ["first"]);

        var pending = fixture.Client.ExecuteAsync<EmbedJobOutput>(
            request,
            TestContext.Current.CancellationToken);
        await fixture.Process.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await fixture.CompleteNextAsync(
            JsonSerializer.SerializeToElement(
                new EmbedJobOutput([[1d, 2d]]),
                LocalAiJson.Strict));

        var result = await pending;

        Assert.Single(result.Value.Embeddings);
        Assert.Equal([1d, 2d], result.Value.Embeddings[0]);
        Assert.Equal(request.JobId, result.Receipt.JobId);
        Assert.Equal(1, fixture.Process.EnsureCalls);
    }

    [Fact]
    public async Task Failure_is_reported_without_exposing_request_content()
    {
        using var fixture = new BrokerClientFixture();
        var request = LocalJobRequestFactory.CreateChat(
            "chat:test",
            LocalJobPriority.Foreground,
            "chat-model",
            "SECRET-PROMPT",
            null,
            []);

        var pending = fixture.Client.ExecuteAsync<ChatJobOutput>(
            request,
            TestContext.Current.CancellationToken);
        await fixture.Process.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await fixture.FailNextAsync("HttpRequestException");

        var error = await Assert.ThrowsAsync<BrokerJobFailedException>(() => pending);
        Assert.Contains("HttpRequestException", error.Message);
        Assert.DoesNotContain("SECRET-PROMPT", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_stops_waiting_without_cancelling_shared_job()
    {
        using var fixture = new BrokerClientFixture();
        using var cancellation = new CancellationTokenSource();
        var request = LocalJobRequestFactory.CreateListModels(
            "models:test",
            LocalJobPriority.Interactive);

        var pending = fixture.Client.ExecuteAsync<ListModelsJobOutput>(
            request,
            cancellation.Token);
        await fixture.Process.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var diagnostic = await fixture.Queue.GetDiagnosticAsync(
            request.JobId,
            TestContext.Current.CancellationToken);
        Assert.Equal(LocalJobState.Queued, diagnostic!.State);
    }

    private sealed class BrokerClientFixture : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "localai-client-" + Guid.NewGuid().ToString("N"));

        public BrokerClientFixture()
        {
            Queue = new DurableQueue(_root);
            Process = new FakeBrokerProcess();
            Client = new BrokerClient(
                Queue,
                Process,
                static (delay, token) => Task.Delay(TimeSpan.FromMilliseconds(5), token),
                TimeSpan.FromSeconds(5));
        }

        public DurableQueue Queue { get; }

        public FakeBrokerProcess Process { get; }

        public BrokerClient Client { get; }

        public async Task CompleteNextAsync(JsonElement body)
        {
            var lease = await Queue.LeaseNextAsync("test-worker")
                ?? throw new InvalidOperationException("Expected queued work.");
            var now = DateTimeOffset.UtcNow;
            var envelope = new BrokerResponseEnvelope(
                body,
                ReceiptFactory.Create(lease.Request, now, now));
            await Queue.CompleteAsync(
                lease.Request.JobId,
                lease.WorkerId,
                lease.LeaseId,
                JsonSerializer.SerializeToElement(envelope, LocalAiJson.Strict));
        }

        public async Task FailNextAsync(string failureCode)
        {
            var lease = await Queue.LeaseNextAsync("test-worker")
                ?? throw new InvalidOperationException("Expected queued work.");
            await Queue.FailAsync(
                lease.Request.JobId,
                lease.WorkerId,
                lease.LeaseId,
                failureCode);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FakeBrokerProcess : IBrokerProcess
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EnsureCalls { get; private set; }

        public Task EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            Started.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
