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

    /// <summary>
    /// The join #277 is about: the runtime measures the shortfall, and the receipt is what
    /// carries it to the line a person reads. Testing the two ends apart — the arithmetic here,
    /// the rendering there — is exactly how the field came to be assigned and read by nobody,
    /// so this asserts the middle.
    /// </summary>
    [Fact]
    public async Task A_partly_offloaded_load_reaches_the_receipt()
    {
        var runtime = new FakeRuntime { SizeBytes = 1000, SizeVramBytes = 420 };
        var coordinator = Create(runtime, (_, _) => Task.FromResult(Result("answer")));

        var result = await coordinator.ExecuteAsync(
            RoutedRequest(),
            Availability(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ResidencyShortfall.PartialOffload, result.Routing!.ResidencyShortfall);
        Assert.Equal(42, result.Routing.VramResidentPercent);
    }

    /// <summary>
    /// And keeps reaching it once the model is warm. EnsureReadyAsync runs only on a cold
    /// start, so a mark read from this call's preflight would appear on the first answer of
    /// every warm window and leave the rest of them looking healthy.
    /// </summary>
    [Fact]
    public async Task A_warm_answer_is_marked_too()
    {
        var runtime = new FakeRuntime { SizeBytes = 1000, SizeVramBytes = 0 };
        var coordinator = Create(runtime, (_, _) => Task.FromResult(Result("answer")));

        await coordinator.ExecuteAsync(
            RoutedRequest("one"),
            Availability(),
            TestContext.Current.CancellationToken);
        var warm = await coordinator.ExecuteAsync(
            RoutedRequest("two"),
            Availability(resident: ["translategemma:12b"]),
            TestContext.Current.CancellationToken);

        Assert.Single(runtime.Preflights);
        Assert.False(warm.Routing!.WasCold);
        Assert.Equal(ResidencyShortfall.Cpu, warm.Routing.ResidencyShortfall);
    }

    /// <summary>A healthy load leaves the receipt unmarked, so the mark keeps meaning something.</summary>
    [Fact]
    public async Task A_fully_resident_load_leaves_the_receipt_unmarked()
    {
        var coordinator = Create(
            new FakeRuntime(),
            (_, _) => Task.FromResult(Result("answer")));

        var result = await coordinator.ExecuteAsync(
            RoutedRequest(),
            Availability(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ResidencyShortfall.None, result.Routing!.ResidencyShortfall);
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
    public async Task Established_candidate_technical_failure_uses_distinct_catalog_fallback()
    {
        var runtime = new FakeRuntime();
        var executedModels = new List<string>();
        var coordinator = Create(
            runtime,
            (request, _) =>
            {
                var model = Assert.IsType<ChatJobPayload>(request.Payload).Model!;
                executedModels.Add(model);
                return model == "qwen3.5:9b"
                    ? Task.FromException<BrokerExecutionResult>(
                        new IOException("model execution failed"))
                    : Task.FromResult(Result("summary"));
            });
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "summary-fallback",
            LocalJobPriority.Foreground,
            LocalTaskProfile.ShortSummary,
            "Summarize supplied facts",
            null,
            [],
            new LocalWorkloadMetadata(
                100,
                120,
                1,
                0,
                0,
                LocalDurationClass.Short),
            workflow: null,
            requestedContextTokens: 2048);
        var availability = new ModelAvailability(
            ["qwen3.5:9b", "qwen2.5-coder:14b"],
            [],
            []);

        var result = await coordinator.ExecuteAsync(
            request,
            availability,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["qwen3.5:9b", "qwen2.5-coder:14b"],
            executedModels);
        Assert.Equal("qwen2.5-coder:14b", result.Routing!.SelectedModel);
        Assert.True(result.Routing.UsedFallback);
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
        Func<ModelSelection, BrokerExecutionResult, ModelValidationResult>? validate = null,
        Action<string>? diagnostic = null) =>
        new(
            new ModelRouter(ModelRoutingCatalog.LoadEmbedded()),
            runtime,
            new ExperimentStateStore(_root),
            new ModelTelemetryStore(_root),
            execute,
            validate,
            diagnostic: diagnostic);

    /// <summary>
    /// Makes every telemetry write fail with IOException: the store's metrics directory is
    /// root\telemetry\metrics, and a file sitting where the telemetry directory should be
    /// defeats Directory.CreateDirectory deterministically. Experiment state is unaffected —
    /// it lives under root\experiments.
    /// </summary>
    private void BreakTelemetry()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "telemetry"), "in the way");
    }

    /// <summary>
    /// Telemetry is measurement, not the answer (#202). A metrics directory that stopped
    /// accepting writes used to turn a finished correct answer into a failed job.
    /// </summary>
    [Fact]
    public async Task A_broken_telemetry_directory_does_not_take_the_answer_with_it()
    {
        BreakTelemetry();
        var diagnostics = new List<string>();
        var coordinator = Create(
            new FakeRuntime(),
            (_, _) => Task.FromResult(Result("translated")),
            diagnostic: diagnostics.Add);

        var result = await coordinator.ExecuteAsync(
            RoutedRequest(),
            Availability(),
            TestContext.Current.CancellationToken);

        Assert.Equal("translategemma:12b", result.Routing!.SelectedModel);
        Assert.False(result.Routing.UsedFallback);
        Assert.Contains(
            diagnostics,
            message => message.Contains("telemetry record", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of #202: a telemetry failure recorded next to a model failure used to
    /// be aggregated into the attempt exception, and the extra fallback that followed was
    /// caused by a disk, not by the model. The fallback the model failure itself defines
    /// must run — exactly once — and nothing more.
    /// </summary>
    [Fact]
    public async Task A_telemetry_failure_after_a_model_failure_stays_a_model_failure()
    {
        BreakTelemetry();
        var diagnostics = new List<string>();
        var executedModels = new List<string>();
        var coordinator = Create(
            new FakeRuntime(),
            (request, _) =>
            {
                var model = Assert.IsType<ChatJobPayload>(request.Payload).Model!;
                executedModels.Add(model);
                return model == "translategemma:12b"
                    ? Task.FromException<BrokerExecutionResult>(
                        new IOException("model execution failed"))
                    : Task.FromResult(Result("translated"));
            },
            diagnostic: diagnostics.Add);

        var result = await coordinator.ExecuteAsync(
            RoutedRequest(),
            Availability(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["translategemma:12b", "qwen3.5:9b"], executedModels);
        Assert.Equal("qwen3.5:9b", result.Routing!.SelectedModel);
        Assert.True(result.Routing.UsedFallback);
        Assert.Equal(
            ModelExecutionOutcome.TechnicalFailure,
            result.Routing.ExperimentalOutcome);
        Assert.Equal(2, diagnostics.Count);
    }

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

        public long SizeBytes { get; init; } = 100;

        public long SizeVramBytes { get; init; } = 100;

        public bool IsDisabled(string model, int contextTokens) => false;

        public Task PullAsync(
            string model,
            IJobProgress? progress,
            CancellationToken cancellationToken = default) =>
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
                SizeBytes,
                SizeVramBytes,
                SizeBytes == SizeVramBytes,
                DateTimeOffset.UtcNow));
        }
    }
}
