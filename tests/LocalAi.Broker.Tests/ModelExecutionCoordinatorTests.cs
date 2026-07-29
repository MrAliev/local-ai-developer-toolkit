using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelExecutionCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-coordinator-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Cold_routed_chat_preflights_then_executes_the_same_immutable_selection()
    {
        var runtime = new FakeRuntime();
        var executedModels = new List<string>();
        var coordinator = Create(
            runtime,
            (request, _) =>
            {
                executedModels.Add(Assert.IsType<ChatJobPayload>(request.Payload).Model!);
                return Task.FromResult(Result("translated"));
            });

        var result = await coordinator.ExecuteAsync(
            RoutedRequest(),
            Availability(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["translategemma:12b:2048"], runtime.Preflights);
        Assert.Equal(["translategemma:12b"], executedModels);
        Assert.Equal("translategemma:12b", result.Routing!.SelectedModel);
        Assert.True(result.Routing.WasCold);
        Assert.False(result.Routing.UsedFallback);
    }

    [Fact]
    public async Task Warm_matching_execution_reuses_a_recent_residency_proof()
    {
        var runtime = new FakeRuntime();
        var coordinator = Create(
            runtime,
            (_, _) => Task.FromResult(Result("translated")));

        await coordinator.ExecuteAsync(
            RoutedRequest("one"),
            Availability(),
            TestContext.Current.CancellationToken);
        var result = await coordinator.ExecuteAsync(
            RoutedRequest("two"),
            Availability(resident: ["translategemma:12b"]),
            TestContext.Current.CancellationToken);

        Assert.Single(runtime.Preflights);
        Assert.False(result.Routing!.WasCold);
    }

    [Fact]
    public async Task Workflow_steps_do_not_count_as_completed_experiment_tasks()
    {
        var runtime = new FakeRuntime();
        var coordinator = Create(
            runtime,
            (_, _) => Task.FromResult(Result("translated")));
        var workflowId = Guid.NewGuid();

        await coordinator.ExecuteAsync(
            RoutedRequest(
                "step-0",
                new LocalWorkflowHint(
                    workflowId,
                    0,
                    2,
                    [
                        LocalTaskProfile.PlainTranslation,
                        LocalTaskProfile.PlainTranslation
                    ],
                    isDependencyReady: true)),
            Availability(),
            TestContext.Current.CancellationToken);
        await coordinator.ExecuteAsync(
            RoutedRequest(
                "step-1",
                new LocalWorkflowHint(
                    workflowId,
                    1,
                    2,
                    [
                        LocalTaskProfile.PlainTranslation,
                        LocalTaskProfile.PlainTranslation
                    ],
                    isDependencyReady: true)),
            Availability(resident: ["translategemma:12b"]),
            TestContext.Current.CancellationToken);

        var state = await new ExperimentStateStore(_root)
            .LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            0,
            state.Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b").CompletedAttempts);
    }

    [Fact]
    public async Task Structural_failure_records_outcome_and_executes_fallback_once()
    {
        var runtime = new FakeRuntime();
        var executedModels = new List<string>();
        var coordinator = Create(
            runtime,
            (request, _) =>
            {
                var model = Assert.IsType<ChatJobPayload>(request.Payload).Model!;
                executedModels.Add(model);
                return Task.FromResult(Result(model));
            },
            (selection, _) => selection.Model == "translategemma:12b"
                ? ModelValidationResult.Fail(
                    ModelExecutionOutcome.StructuralFailure,
                    "structure")
                : ModelValidationResult.Pass("structure"));

        var result = await coordinator.ExecuteAsync(
            RoutedRequest(),
            Availability(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["translategemma:12b", "qwen3.5:9b"], executedModels);
        Assert.Equal("qwen3.5:9b", result.Routing!.SelectedModel);
        Assert.True(result.Routing.UsedFallback);
        var state = await new ExperimentStateStore(_root)
            .LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            1,
            state.Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b").StructuralFailures);
    }

    [Fact]
    public async Task Technical_execution_failure_records_telemetry_and_executes_fallback_once()
    {
        var runtime = new FakeRuntime();
        var executedModels = new List<string>();
        var coordinator = Create(
            runtime,
            (request, _) =>
            {
                var model = Assert.IsType<ChatJobPayload>(request.Payload).Model!;
                executedModels.Add(model);
                return model == "translategemma:12b"
                    ? Task.FromException<BrokerExecutionResult>(
                        new IOException("model execution failed"))
                    : Task.FromResult(Result("translated"));
            });

        var result = await coordinator.ExecuteAsync(
            RoutedRequest(),
            Availability(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["translategemma:12b", "qwen3.5:9b"], executedModels);
        Assert.Equal("qwen3.5:9b", result.Routing!.SelectedModel);
        Assert.True(result.Routing.UsedFallback);
        Assert.Equal(
            "translategemma:12b",
            result.Routing.ExperimentalModel);
        Assert.Equal(
            ModelExecutionOutcome.TechnicalFailure,
            result.Routing.ExperimentalOutcome);
        var state = await new ExperimentStateStore(_root)
            .LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            1,
            state.Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b").TechnicalFailures);
        var telemetry = await new ModelTelemetryStore(_root)
            .ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [
                ModelExecutionOutcome.TechnicalFailure,
                ModelExecutionOutcome.Success
            ],
            telemetry.Select(record => record.Outcome));
    }

    [Fact]
    public async Task Workflow_technical_failure_is_reported_without_counting_a_chunk()
    {
        var coordinator = Create(
            new FakeRuntime(),
            (request, _) =>
            {
                var model = Assert.IsType<ChatJobPayload>(request.Payload).Model!;
                return model == "translategemma:12b"
                    ? Task.FromException<BrokerExecutionResult>(
                        new IOException("model execution failed"))
                    : Task.FromResult(Result("translated"));
            });
        var request = RoutedRequest(
            "workflow-failure",
            new LocalWorkflowHint(
                Guid.NewGuid(),
                0,
                1,
                [LocalTaskProfile.PlainTranslation],
                isDependencyReady: true));

        var result = await coordinator.ExecuteAsync(
            request,
            Availability(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ModelExecutionOutcome.TechnicalFailure,
            result.Routing!.ExperimentalOutcome);
        var state = await new ExperimentStateStore(_root)
            .LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            0,
            state.Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b").CompletedAttempts);
    }

    [Fact]
    public async Task Post_execution_validation_exception_does_not_repeat_ambiguous_work()
    {
        var executedModels = new List<string>();
        var coordinator = Create(
            new FakeRuntime(),
            (request, _) =>
            {
                executedModels.Add(
                    Assert.IsType<ChatJobPayload>(request.Payload).Model!);
                return Task.FromResult(Result("translated"));
            },
            (_, _) => throw new InvalidOperationException("validator fault"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(
                RoutedRequest(),
                Availability(),
                TestContext.Current.CancellationToken));

        Assert.Equal("validator fault", exception.Message);
        Assert.Equal(["translategemma:12b"], executedModels);
        var state = await new ExperimentStateStore(_root)
            .LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            0,
            state.Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b").CompletedAttempts);
    }

    [Fact]
    public async Task Full_vram_preflight_failure_never_sends_task_content()
    {
        var runtime = new FakeRuntime
        {
            Failure = new ModelPreflightException(
                "translategemma:12b",
                2048,
                ModelExecutionOutcome.CpuOffload,
                "partial residency")
        };
        var executions = 0;
        var coordinator = Create(
            runtime,
            (_, _) =>
            {
                executions++;
                return Task.FromResult(Result("must not run"));
            });

        await Assert.ThrowsAsync<ModelPreflightException>(
            () => coordinator.ExecuteAsync(
                RoutedRequest(),
                new ModelAvailability(
                    ["translategemma:12b"],
                    [],
                    [new ModelContextRef("qwen3.5:9b", 2048)]),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, executions);
    }

    private ModelExecutionCoordinator Create(
        IModelRuntime runtime,
        Func<LocalJobRequest, CancellationToken, Task<BrokerExecutionResult>> execute,
        Func<ModelSelection, BrokerExecutionResult, ModelValidationResult>? validate = null) =>
        new(
            new ModelRouter(ModelRoutingCatalog.LoadEmbedded()),
            runtime,
            new ExperimentStateStore(_root),
            new ModelTelemetryStore(_root),
            execute,
            validate);

    private static LocalJobRequest RoutedRequest(
        string key = "translate",
        LocalWorkflowHint? workflow = null) =>
        LocalJobRequestFactory.CreateRoutedChat(
            key,
            LocalJobPriority.Foreground,
            LocalTaskProfile.PlainTranslation,
            "Translate text",
            null,
            [],
            new LocalWorkloadMetadata(
                100,
                120,
                0,
                0,
                0,
                LocalDurationClass.Short),
            workflow,
            requestedContextTokens: 2048);

    private static ModelAvailability Availability(
        IReadOnlyList<string>? resident = null) =>
        new(
            ["translategemma:12b", "qwen3.5:9b"],
            resident ?? [],
            []);

    private static BrokerExecutionResult Result(string value) =>
        new(JsonSerializer.SerializeToElement(new ChatJobOutput(value)));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeRuntime : IModelRuntime
    {
        public List<string> Preflights { get; } = [];

        public ModelPreflightException? Failure { get; init; }

        public bool IsDisabled(string model, int contextTokens) => false;

        public Task PullAsync(string model, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ModelResidencyProof> EnsureReadyAsync(
            string model,
            int contextTokens,
            CancellationToken cancellationToken = default)
        {
            Preflights.Add($"{model}:{contextTokens}");
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new ModelResidencyProof(
                model,
                contextTokens,
                100,
                100,
                true,
                DateTimeOffset.UtcNow));
        }
    }
}
