using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class BrokerLocalModelClientTests
{
    [Fact]
    public async Task Chat_routes_images_priority_and_returns_common_receipt()
    {
        var broker = new FakeBrokerClient(new ChatJobOutput("answer"));
        var client = new BrokerLocalModelClient(broker);

        var result = await client.ChatAsync(
            "vision-model",
            "question",
            "system",
            ["base64-image"],
            LocalJobPriority.Foreground,
            TestContext.Current.CancellationToken);

        var request = Assert.IsType<LocalJobRequest>(broker.Request);
        var payload = Assert.IsType<ChatJobPayload>(request.Payload);
        Assert.Equal("vision-model", payload.Model);
        Assert.Equal("question", payload.Prompt);
        Assert.Equal("system", payload.System);
        Assert.Equal(["base64-image"], payload.ImagesBase64);
        Assert.Equal(LocalJobPriority.Foreground, request.Priority);
        Assert.Equal("answer", result.Value);
        Assert.Equal(request.JobId, result.Receipt.JobId);
    }

    [Fact]
    public async Task List_models_returns_read_only_values_and_receipt()
    {
        var broker = new FakeBrokerClient(
            new ListModelsJobOutput(["model-a", "model-b"]));
        var client = new BrokerLocalModelClient(broker);

        var result = await client.ListModelsAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["model-a", "model-b"], result.Value);
        Assert.IsType<ListModelsJobPayload>(broker.Request!.Payload);
        Assert.Equal(broker.Request.JobId, result.Receipt.JobId);
    }

    private sealed class FakeBrokerClient(object output) : IBrokerClient
    {
        public LocalJobRequest? Request { get; private set; }

        public Task<LocalJobResult<T>> ExecuteAsync<T>(
            LocalJobRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var receipt = new LocalUsageReceipt(
                request.JobId,
                "local-lm",
                request.Kind == LocalJobKind.Chat ? "chat" : "list-models",
                request.Payload is ChatJobPayload chat ? chat.Model : "n/a",
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                0,
                null,
                null,
                null);
            return Task.FromResult(new LocalJobResult<T>((T)output, receipt));
        }
    }
}
