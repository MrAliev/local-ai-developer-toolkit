using System.Net;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerReceiptTests
{
    [Fact]
    public void Receipt_uses_bounded_queue_and_execution_timing()
    {
        var created = new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
        var request = LocalJobRequestFactory.CreateChat(
            "receipt",
            LocalJobPriority.Foreground,
            "chat-model",
            "12345678",
            "12",
            [],
            createdAtUtc: created);

        var receipt = ReceiptFactory.Create(
            request,
            created.AddSeconds(2),
            created.AddSeconds(5));

        Assert.Equal("local-lm", receipt.Tool);
        Assert.Equal("chat", receipt.Operation);
        Assert.Equal("chat-model", receipt.Model);
        Assert.Equal(TimeSpan.FromSeconds(2), receipt.QueueDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), receipt.ExecutionDuration);
        Assert.Equal(10, receipt.InputCharacters);
        Assert.Equal(3, receipt.EstimatedCloudTokensSaved);
    }

    [Fact]
    public void Receipt_uses_routed_model_and_preserves_content_free_routing_metrics()
    {
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "routed-receipt",
            LocalJobPriority.Foreground,
            LocalTaskProfile.PlainTranslation,
            "translate",
            null,
            [],
            new LocalWorkloadMetadata(
                100,
                100,
                0,
                0,
                0,
                LocalDurationClass.Short),
            requestedContextTokens: 2048);
        var routing = new LocalRoutingReceipt(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            2048,
            WasCold: true,
            UsedFallback: false,
            "structure:pass",
            10,
            15,
            85);

        var receipt = ReceiptFactory.Create(
            request,
            request.CreatedAtUtc,
            request.CreatedAtUtc.AddSeconds(1),
            routing);

        Assert.Equal("translategemma:12b", receipt.Model);
        Assert.Equal(routing, receipt.Routing);
        Assert.Equal(85, receipt.EstimatedCloudTokensSaved);
    }

    [Fact]
    public async Task Concurrent_deduplicated_clients_make_one_fake_ollama_request()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"models":[{"name":"model-a"}]}""");
        using var httpClient = new HttpClient(fake);
        using var transport = new OllamaTransport(
            httpClient,
            new Uri("http://ollama.test:11434/"));
        using var shutdown = new CancellationTokenSource();
        var process = new RunningProcess();
        var firstClient = new BrokerClient(queue, process);
        var secondClient = new BrokerClient(queue, process);
        var firstRequest = LocalJobRequestFactory.CreateListModels(
            "shared-model-list",
            LocalJobPriority.Interactive);
        var secondRequest = LocalJobRequestFactory.CreateListModels(
            "shared-model-list",
            LocalJobPriority.Interactive);
        var firstEnqueue = await queue.EnqueueAsync(
            firstRequest,
            TestContext.Current.CancellationToken);
        var secondEnqueue = await queue.EnqueueAsync(
            secondRequest,
            TestContext.Current.CancellationToken);
        Assert.False(firstEnqueue.JoinedExisting);
        Assert.True(secondEnqueue.JoinedExisting);
        Assert.Equal(firstEnqueue.JobId, secondEnqueue.JobId);
        var first = firstClient.ExecuteAsync<ListModelsJobOutput>(
            firstRequest,
            TestContext.Current.CancellationToken);
        var second = secondClient.ExecuteAsync<ListModelsJobOutput>(
            secondRequest,
            TestContext.Current.CancellationToken);
        var host = new BrokerHost(
            queue,
            "receipt-worker",
            (request, _, token) => transport.ExecuteAsync(request, token));
        var hostTask = host.RunAsync(shutdown.Token);
        var results = await Task.WhenAll(first, second);
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostTask);

        Assert.Single(fake.Requests);
        Assert.All(results, result => Assert.Equal(["model-a"], result.Value.Models));
        Assert.Equal(results[0].Receipt.JobId, results[1].Receipt.JobId);
    }

    [Fact]
    public async Task Successful_execution_reports_actual_duration_for_learning()
    {
        using var root = new TemporaryRuntimeRoot();
        var now = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var queue = new DurableQueue(root.Path, clock);
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "duration-learning",
            LocalJobPriority.Foreground,
            LocalTaskProfile.CodeAnalysis,
            "analyze",
            null,
            [],
            new LocalWorkloadMetadata(
                7,
                20,
                1,
                0,
                0,
                LocalDurationClass.Short),
            createdAtUtc: now);
        await queue.EnqueueAsync(request, TestContext.Current.CancellationToken);
        var routing = new LocalRoutingReceipt(
            LocalTaskProfile.CodeAnalysis,
            "qwen2.5-coder:14b",
            4096,
            WasCold: false,
            UsedFallback: false,
            "references:pass",
            10,
            1,
            9);
        var observations =
            new List<(LocalJobRequest Request, LocalRoutingReceipt? Routing, TimeSpan Duration)>();
        using var shutdown = new CancellationTokenSource();
        var host = new BrokerHost(
            queue,
            "duration-worker",
            (_, _, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(7));
                return Task.FromResult(new BrokerExecutionResult(
                    JsonSerializer.SerializeToElement(new ChatJobOutput("done")),
                    routing));
            },
            clock,
            idleDelay: (_, token) => Task.Delay(1, token),
            durationObserver: (completedRequest, completedRouting, duration) =>
                observations.Add((completedRequest, completedRouting, duration)));

        var hostTask = host.RunAsync(shutdown.Token);
        while (await queue.ReadResponseAsync(
                   request.JobId,
                   TestContext.Current.CancellationToken) is null)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostTask);
        var observation = Assert.Single(observations);
        Assert.Equal(request, observation.Request);
        Assert.Same(routing, observation.Routing);
        Assert.Equal(TimeSpan.FromSeconds(7), observation.Duration);
    }

    private sealed class RunningProcess : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
