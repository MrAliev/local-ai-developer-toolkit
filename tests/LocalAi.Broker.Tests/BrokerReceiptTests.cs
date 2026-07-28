using System.Net;
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
        var host = new BrokerHost(
            queue,
            "receipt-worker",
            transport.ExecuteAsync);
        var hostTask = host.RunAsync(shutdown.Token);
        var process = new RunningProcess();
        var firstClient = new BrokerClient(queue, process);
        var secondClient = new BrokerClient(queue, process);
        var firstRequest = LocalJobRequestFactory.CreateListModels(
            "shared-model-list",
            LocalJobPriority.Interactive);
        var secondRequest = LocalJobRequestFactory.CreateListModels(
            "shared-model-list",
            LocalJobPriority.Interactive);

        var first = firstClient.ExecuteAsync<ListModelsJobOutput>(
            firstRequest,
            TestContext.Current.CancellationToken);
        var second = secondClient.ExecuteAsync<ListModelsJobOutput>(
            secondRequest,
            TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(first, second);
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostTask);

        Assert.Single(fake.Requests);
        Assert.All(results, result => Assert.Equal(["model-a"], result.Value.Models));
        Assert.Equal(results[0].Receipt.JobId, results[1].Receipt.JobId);
    }

    private sealed class RunningProcess : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
