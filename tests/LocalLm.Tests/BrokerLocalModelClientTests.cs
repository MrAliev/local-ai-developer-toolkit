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
    public async Task Routed_chat_sends_profile_workload_workflow_and_context()
    {
        var broker = new FakeBrokerClient(new ChatJobOutput("answer"));
        var client = new BrokerLocalModelClient(broker);
        var workflowId = Guid.NewGuid();

        await client.RoutedChatAsync(
            LocalTaskProfile.TechnicalTranslation,
            "translate",
            "preserve",
            null,
            new LocalWorkloadMetadata(
                100,
                120,
                1,
                0,
                0,
                LocalDurationClass.Short),
            new LocalWorkflowHint(
                workflowId,
                0,
                1,
                [LocalTaskProfile.TechnicalTranslation],
                true),
            modelOverride: null,
            requestedContextTokens: 2048,
            LocalJobPriority.Foreground,
            TestContext.Current.CancellationToken);

        var payload = Assert.IsType<ChatJobPayload>(broker.Request!.Payload);
        Assert.Null(payload.Model);
        Assert.Equal(LocalTaskProfile.TechnicalTranslation, payload.TaskProfile);
        Assert.Equal(100, payload.Workload!.InputCharacters);
        Assert.Equal(workflowId, payload.Workflow!.WorkflowId);
        Assert.Equal(2048, payload.RequestedContextTokens);
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

    [Fact]
    public async Task Model_control_operations_use_typed_durable_jobs()
    {
        var statusOutput = new LocalModelsStatusOutput(
            ["qwen3.5:9b"],
            [],
            ["translategemma:12b"],
            [],
            "1");
        var statusBroker = new FakeBrokerClient(statusOutput);
        var statusClient = new BrokerLocalModelClient(statusBroker);

        var status = await statusClient.GetModelsStatusAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(statusOutput, status.Value);
        var payload = Assert.IsType<ModelControlJobPayload>(
            statusBroker.Request!.Payload);
        Assert.Equal(ModelControlOperation.Status, payload.Operation);

        var pullBroker = new FakeBrokerClient(
            new ModelMaintenanceJobOutput("success"));
        var pullClient = new BrokerLocalModelClient(pullBroker);
        await pullClient.PullModelAsync(
            "translategemma:12b",
            "1",
            TestContext.Current.CancellationToken);
        Assert.IsType<ModelMaintenanceJobPayload>(pullBroker.Request!.Payload);
        Assert.Equal(LocalJobPriority.Background, pullBroker.Request.Priority);
    }

    [Fact]
    public async Task Model_preflight_uses_a_typed_content_free_control_job()
    {
        var output = new LocalModelPreflightOutput(
            "translategemma:12b",
            2048,
            100,
            100,
            true,
            DateTimeOffset.UtcNow);
        var broker = new FakeBrokerClient(output);
        var client = new BrokerLocalModelClient(broker);

        var result = await client.PreflightModelAsync(
            "translategemma:12b",
            2048,
            TestContext.Current.CancellationToken);

        Assert.Equal(output, result.Value);
        var payload = Assert.IsType<ModelControlJobPayload>(
            broker.Request!.Payload);
        Assert.Equal(ModelControlOperation.Preflight, payload.Operation);
        Assert.Equal("translategemma:12b", payload.Model);
        Assert.Equal(2048, payload.ContextTokens);
        Assert.Null(payload.Profile);
    }

    [Fact]
    public async Task Experiment_completion_uses_one_typed_idempotent_control_job()
    {
        var workflowId = Guid.NewGuid();
        var output = new LocalExperimentCompletionOutput(
            workflowId,
            LocalTaskProfile.TechnicalTranslation,
            "translategemma:12b",
            ModelExecutionOutcome.StructuralFailure);
        var broker = new FakeBrokerClient(output);
        var client = new BrokerLocalModelClient(broker);
        var metrics = new LocalExperimentTaskMetrics(
            2_500,
            4_800,
            7_300,
            4_800,
            0,
            TimeSpan.FromSeconds(45),
            1,
            8,
            true);

        var result = await client.CompleteExperimentAsync(
            workflowId,
            LocalTaskProfile.TechnicalTranslation,
            "translategemma:12b",
            ModelExecutionOutcome.StructuralFailure,
            metrics,
            TestContext.Current.CancellationToken);

        Assert.Equal(output, result.Value);
        var request = Assert.IsType<LocalJobRequest>(broker.Request);
        var payload = Assert.IsType<ModelControlJobPayload>(request.Payload);
        Assert.Equal(ModelControlOperation.CompleteExperiment, payload.Operation);
        Assert.Equal(workflowId, payload.WorkflowId);
        Assert.Equal(ModelExecutionOutcome.StructuralFailure, payload.Outcome);
        Assert.Equal(metrics, payload.TaskMetrics);
        Assert.Contains(workflowId.ToString("N"), request.DeduplicationKey);
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
                request.Payload is ChatJobPayload chat ? chat.Model ?? "routed" : "n/a",
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
