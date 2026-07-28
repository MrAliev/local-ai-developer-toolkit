using CodeSearch.Core.Embedding;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class BrokerEmbeddingClientTests
{
    [Fact]
    public async Task Routes_model_priority_and_deduplication_key_through_broker()
    {
        var broker = new FakeBrokerClient([[3, 4]]);
        var client = new BrokerEmbeddingClient("embed-model", broker);

        var vectors = await client.EmbedAsync(
            ["text"],
            LocalJobPriority.Background,
            "index:tree:batch-1",
            TestContext.Current.CancellationToken);

        var request = Assert.IsType<LocalJobRequest>(broker.Request);
        var payload = Assert.IsType<EmbedJobPayload>(request.Payload);
        Assert.Equal("embed-model", payload.Model);
        Assert.Equal(["text"], payload.Inputs);
        Assert.Equal(LocalJobPriority.Background, request.Priority);
        Assert.Equal("index:tree:batch-1", request.DeduplicationKey);
        Assert.Equal(0.6f, vectors[0][0], 5);
        Assert.Equal(0.8f, vectors[0][1], 5);
    }

    [Fact]
    public async Task Rejects_response_count_mismatch()
    {
        var client = new BrokerEmbeddingClient(
            "embed-model",
            new FakeBrokerClient([[1, 0]]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EmbedAsync(
                ["first", "second"],
                LocalJobPriority.Interactive,
                "query:tree",
                TestContext.Current.CancellationToken));
    }

    private sealed class FakeBrokerClient(
        IReadOnlyList<IReadOnlyList<double>> embeddings) : IBrokerClient
    {
        public LocalJobRequest? Request { get; private set; }

        public Task<LocalJobResult<T>> ExecuteAsync<T>(
            LocalJobRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var output = (T)(object)new EmbedJobOutput(embeddings);
            var receipt = new LocalUsageReceipt(
                request.JobId,
                "code-search",
                "embed",
                "embed-model",
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                0,
                null,
                null,
                null);
            return Task.FromResult(new LocalJobResult<T>(output, receipt));
        }
    }
}
