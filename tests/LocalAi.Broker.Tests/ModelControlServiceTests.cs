using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelControlServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-control-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Status_reads_live_installed_resident_and_experiment_state()
    {
        var transport = new FakeTransport
        {
            Installed = ["qwen3.5:9b"],
            Processes =
            [
                new OllamaProcessInfo(
                    "qwen3.5:9b",
                    100,
                    100,
                    2048,
                    DateTimeOffset.UtcNow.AddMinutes(5))
            ]
        };
        var experiments = new ExperimentStateStore(_root);
        var queue = new DurableQueue(_root);
        await queue.EnqueueAsync(
            LocalJobRequestFactory.CreateModelMaintenance(
                "pending-pull",
                LocalJobPriority.Background,
                ModelMaintenanceOperation.Pull,
                "translategemma:12b",
                "1"),
            TestContext.Current.CancellationToken);
        await experiments.SaveAsync(
            ExperimentSnapshot.Empty.Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success),
            TestContext.Current.CancellationToken);
        var service = new ModelControlService(
            ModelRoutingCatalog.LoadEmbedded(),
            transport,
            experiments,
            new ModelTelemetryStore(_root),
            runtime: null,
            queue);

        var status = await service.StatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["qwen3.5:9b"], status.InstalledModels);
        Assert.Equal(["qwen3.5:9b"], status.ResidentModels);
        Assert.Equal(["translategemma:12b"], status.RecommendedMissingModels);
        var residency = Assert.Single(status.Residency!);
        Assert.Equal(100, residency.SizeBytes);
        Assert.Equal(100, residency.SizeVramBytes);
        Assert.True(residency.FullyResident);
        Assert.Equal(["translategemma:12b"], status.PendingPullModels);
        Assert.Equal(1, Assert.Single(status.Experiments).CompletedAttempts);
    }

    [Fact]
    public async Task Feedback_updates_only_the_requested_profile_model_pair()
    {
        var experiments = new ExperimentStateStore(_root);
        await experiments.SaveAsync(
            PausedState(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b"),
            TestContext.Current.CancellationToken);
        var service = new ModelControlService(
            ModelRoutingCatalog.LoadEmbedded(),
            new FakeTransport(),
            experiments,
            new ModelTelemetryStore(_root));

        var result = await service.ApplyFeedbackAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ExperimentOwnerAction.Promote,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExperimentOwnerAction.Promote, result.OwnerAction);
        Assert.True(result.IsPromoted);
        Assert.False(
            (await experiments.LoadAsync(TestContext.Current.CancellationToken))
            .Pair(
                LocalTaskProfile.TechnicalTranslation,
                "translategemma:12b").IsPromoted);
    }

    [Theory]
    [InlineData(ExperimentOwnerAction.Promote)]
    [InlineData(ExperimentOwnerAction.ContinueExperiment)]
    [InlineData(ExperimentOwnerAction.FallbackOnly)]
    [InlineData(ExperimentOwnerAction.Disable)]
    public async Task Feedback_is_rejected_before_the_ten_task_report_gate(
        ExperimentOwnerAction action)
    {
        var experiments = new ExperimentStateStore(_root);
        var service = new ModelControlService(
            ModelRoutingCatalog.LoadEmbedded(),
            new FakeTransport(),
            experiments,
            new ModelTelemetryStore(_root));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyFeedbackAsync(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                action,
                TestContext.Current.CancellationToken));

        Assert.Contains("10", exception.Message, StringComparison.Ordinal);
        Assert.Null(
            (await experiments.LoadAsync(TestContext.Current.CancellationToken))
            .Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b").OwnerAction);
    }

    [Fact]
    public async Task Continue_experiment_can_reset_an_early_open_circuit()
    {
        var experiments = new ExperimentStateStore(_root);
        var state = ExperimentSnapshot.Empty
            .Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.TechnicalFailure)
            .Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.TechnicalFailure);
        await experiments.SaveAsync(
            state,
            TestContext.Current.CancellationToken);
        var service = new ModelControlService(
            ModelRoutingCatalog.LoadEmbedded(),
            new FakeTransport(),
            experiments,
            new ModelTelemetryStore(_root));

        var result = await service.ApplyFeedbackAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ExperimentOwnerAction.ContinueExperiment,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsCircuitOpen);
        Assert.Equal(0, result.CompletedAttempts);
        Assert.Equal(
            ExperimentOwnerAction.ContinueExperiment,
            result.OwnerAction);
    }

    [Fact]
    public async Task Completing_a_workflow_records_exactly_one_experiment_task()
    {
        var experiments = new ExperimentStateStore(_root);
        var telemetry = new ModelTelemetryStore(_root);
        var service = new ModelControlService(
            ModelRoutingCatalog.LoadEmbedded(),
            new FakeTransport(),
            experiments,
            telemetry);
        var workflowId = Guid.NewGuid();
        var metrics = new LocalExperimentTaskMetrics(
            InputTokens: 2_500,
            OutputTokens: 4_800,
            LocalTokensProcessed: 7_300,
            EstimatedCloudGenerationTokensSaved: 4_800,
            EstimatedNetCloudContextTokensSaved: 0,
            TotalDuration: TimeSpan.FromSeconds(45),
            ColdExecutions: 1,
            WarmExecutions: 8,
            UsedFallback: true);

        await service.CompleteExperimentAsync(
            workflowId,
            LocalTaskProfile.TechnicalTranslation,
            "translategemma:12b",
            ModelExecutionOutcome.StructuralFailure,
            metrics,
            TestContext.Current.CancellationToken);
        await service.CompleteExperimentAsync(
            workflowId,
            LocalTaskProfile.TechnicalTranslation,
            "translategemma:12b",
            ModelExecutionOutcome.StructuralFailure,
            metrics,
            TestContext.Current.CancellationToken);

        var state = await experiments.LoadAsync(
            TestContext.Current.CancellationToken);
        var pair = state.Pair(
            LocalTaskProfile.TechnicalTranslation,
            "translategemma:12b");
        Assert.Equal(1, pair.CompletedAttempts);
        Assert.Equal(1, pair.StructuralFailures);
        var task = Assert.Single(
            await telemetry.ReadExperimentTasksAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(workflowId, task.WorkflowId);
        Assert.Equal(7_300, task.LocalTokensProcessed);
        Assert.Equal(4_800, task.EstimatedCloudGenerationTokensSaved);
        Assert.Equal(0, task.EstimatedNetCloudContextTokensSaved);
        var report = await service.ReportAsync(
            LocalTaskProfile.TechnicalTranslation,
            "translategemma:12b",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, report.ColdExecutions);
        Assert.Equal(8, report.WarmExecutions);
    }

    [Fact]
    public async Task The_report_counts_attempts_the_way_the_status_does()
    {
        var experiments = new ExperimentStateStore(_root);
        var telemetry = new ModelTelemetryStore(_root);
        var service = new ModelControlService(
            ModelRoutingCatalog.LoadEmbedded(),
            new FakeTransport(),
            experiments,
            telemetry);
        var metrics = new LocalExperimentTaskMetrics(
            InputTokens: 100,
            OutputTokens: 100,
            LocalTokensProcessed: 200,
            EstimatedCloudGenerationTokensSaved: 100,
            EstimatedNetCloudContextTokensSaved: 0,
            TotalDuration: TimeSpan.FromSeconds(1),
            ColdExecutions: 1,
            WarmExecutions: 0,
            UsedFallback: false);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await service.CompleteExperimentAsync(
                Guid.NewGuid(),
                LocalTaskProfile.TechnicalTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success,
                metrics,
                TestContext.Current.CancellationToken);
        }

        // Five of the six measurements are gone — the shape a machine is in after the week-long
        // bound has passed over them. The counts must not go with them: they are what the router
        // reads to decide when the experiment pauses, and a status that says six beside a report
        // that says one is not two views of an experiment, it is one of them being wrong.
        foreach (var path in Directory
                     .GetFiles(telemetry.ExperimentTasksDirectory, "*.json")
                     .Order(StringComparer.Ordinal)
                     .Take(5))
        {
            File.Delete(path);
        }

        var report = await service.ReportAsync(
            LocalTaskProfile.TechnicalTranslation,
            "translategemma:12b",
            TestContext.Current.CancellationToken);
        var state = await experiments.LoadAsync(TestContext.Current.CancellationToken);
        var pair = state.Pair(LocalTaskProfile.TechnicalTranslation, "translategemma:12b");

        Assert.Equal(pair.CompletedAttempts, report.Attempts);
        Assert.Equal(6, report.Attempts);
        Assert.Equal(6, report.Successes);
        Assert.Equal(0, report.Errors);
        // And the measurements say honestly how much they rest on.
        Assert.Equal(1, report.ObservedTasks);
        Assert.Equal(1, report.ColdExecutions);
    }

    [Fact]
    public async Task Report_aggregates_content_free_timing_and_net_savings()
    {
        var telemetry = new ModelTelemetryStore(_root);
        var service = new ModelControlService(
            ModelRoutingCatalog.LoadEmbedded(),
            new FakeTransport(),
            new ExperimentStateStore(_root),
            telemetry);
        await service.CompleteExperimentAsync(
            Guid.NewGuid(),
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ModelExecutionOutcome.Success,
            new LocalExperimentTaskMetrics(
                100,
                120,
                220,
                120,
                0,
                TimeSpan.FromSeconds(3),
                ColdExecutions: 1,
                WarmExecutions: 0,
                UsedFallback: false),
            TestContext.Current.CancellationToken);

        var report = await service.ReportAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Successes);
        Assert.Equal(TimeSpan.FromSeconds(3), report.MeanTotalDuration);
        Assert.Equal(220, report.LocalTokensProcessed);
        Assert.Equal(120, report.EstimatedCloudGenerationTokensSaved);
        Assert.Equal(0, report.EstimatedNetCloudContextTokensSaved);
    }

    [Fact]
    public async Task Report_contains_only_the_current_ten_task_batch()
    {
        var experiments = new ExperimentStateStore(_root);
        var telemetry = new ModelTelemetryStore(_root);
        var service = new ModelControlService(
            ModelRoutingCatalog.LoadEmbedded(),
            new FakeTransport(),
            experiments,
            telemetry);
        var metrics = new LocalExperimentTaskMetrics(
            100,
            100,
            200,
            100,
            0,
            TimeSpan.FromSeconds(1),
            0,
            1,
            false);
        for (var attempt = 0; attempt < ExperimentPairState.BatchSize; attempt++)
        {
            await service.CompleteExperimentAsync(
                Guid.NewGuid(),
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success,
                metrics,
                TestContext.Current.CancellationToken);
        }

        await service.ApplyFeedbackAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ExperimentOwnerAction.ContinueExperiment,
            TestContext.Current.CancellationToken);
        await service.CompleteExperimentAsync(
            Guid.NewGuid(),
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ModelExecutionOutcome.StructuralFailure,
            metrics,
            TestContext.Current.CancellationToken);

        var report = await service.ReportAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Attempts);
        Assert.Equal(0, report.Successes);
        Assert.Equal(1, report.Errors);
    }

    private static ExperimentSnapshot PausedState(
        LocalTaskProfile profile,
        string model)
    {
        var state = ExperimentSnapshot.Empty;
        for (var attempt = 0; attempt < ExperimentPairState.BatchSize; attempt++)
        {
            state = state.Record(
                profile,
                model,
                ModelExecutionOutcome.Success);
        }

        return state;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeTransport : IModelRuntimeTransport
    {
        public IReadOnlyList<string> Installed { get; init; } = [];
        public IReadOnlyList<OllamaProcessInfo> Processes { get; init; } = [];
        public Task<IReadOnlyList<string>> ListInstalledAsync(CancellationToken ct) =>
            Task.FromResult(Installed);
        public Task<IReadOnlyList<OllamaProcessInfo>> ListProcessesAsync(CancellationToken ct) =>
            Task.FromResult(Processes);
        public Task PullAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public Task PreflightAsync(string model, int contextTokens, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PreflightEmbeddingAsync(
            string model,
            int contextTokens,
            CancellationToken ct) =>
            Task.CompletedTask;
        public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;
    }
}
