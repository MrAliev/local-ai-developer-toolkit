using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class ModelManagementTasksTests
{
    [Fact]
    public async Task Sync_pulls_only_recommended_missing_models_sequentially()
    {
        var client = new FakeClient
        {
            Status = new LocalModelsStatusOutput(
                ["qwen3.5:9b"],
                [],
                ["translategemma:12b"],
                [],
                "1")
        };
        var tasks = new ModelManagementTasks(client);

        var result = await tasks.SyncRecommendedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["translategemma:12b"], client.Pulled);
        Assert.Equal(["translategemma:12b"], result.InstalledModels);
        Assert.Equal("1", result.CatalogVersion);
        Assert.Equal(1, client.MaxConcurrentPulls);
    }

    [Fact]
    public async Task Feedback_and_report_are_forwarded_as_typed_operations()
    {
        var client = new FakeClient();
        var tasks = new ModelManagementTasks(client);

        await tasks.ApplyFeedbackAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ExperimentOwnerAction.ContinueExperiment,
            TestContext.Current.CancellationToken);
        await tasks.GetExperimentReportAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ExperimentOwnerAction.ContinueExperiment),
            client.Feedback);
        Assert.Equal(
            (LocalTaskProfile.PlainTranslation, "translategemma:12b"),
            client.Report);
    }

    [Fact]
    public async Task Preflight_is_forwarded_without_task_content()
    {
        var client = new FakeClient();
        var tasks = new ModelManagementTasks(client);

        var result = await tasks.PreflightAsync(
            "translategemma:12b",
            2048,
            "signed-7",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ("translategemma:12b", 2048, "signed-7"),
            client.Preflight);
        Assert.True(result.FullyResident);
        Assert.Equal(result.SizeBytes, result.SizeVramBytes);
    }

    private sealed class FakeClient : ILocalModelClient
    {
        private int _activePulls;

        public LocalModelsStatusOutput Status { get; init; } =
            new([], [], [], [], "1");

        public List<string> Pulled { get; } = [];

        public int MaxConcurrentPulls { get; private set; }

        public (
            LocalTaskProfile Profile,
            string Model,
            ExperimentOwnerAction Action)? Feedback { get; private set; }

        public (LocalTaskProfile Profile, string Model)? Report { get; private set; }

        public (string Model, int ContextTokens, string CatalogVersion)? Preflight { get; private set; }

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result(Status));

        public async Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activePulls);
            MaxConcurrentPulls = Math.Max(MaxConcurrentPulls, active);
            await Task.Yield();
            Pulled.Add(model);
            Interlocked.Decrement(ref _activePulls);
            return Result(new ModelMaintenanceJobOutput("success"));
        }

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default)
        {
            Preflight = (model, contextTokens, catalogVersion);
            return Task.FromResult(Result(
                new LocalModelPreflightOutput(
                    model,
                    contextTokens,
                    catalogVersion,
                    100,
                    100,
                    true,
                    DateTimeOffset.UtcNow)));
        }

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile,
            string model,
            CancellationToken cancellationToken = default)
        {
            Report = (profile, model);
            return Task.FromResult(Result(
                new LocalExperimentReportOutput(
                    profile,
                    model,
                    0,
                    0,
                    0,
                    0,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    0,
                    0,
                    0)));
        }

        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default)
        {
            Feedback = (profile, model, action);
            return Task.FromResult(Result(
                new LocalModelFeedbackOutput(profile, model, action)));
        }

        public Task<LocalJobResult<string>> ChatAsync(
            string model,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<string>> RoutedChatAsync(
            LocalTaskProfile profile,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalWorkloadMetadata workload,
            LocalWorkflowHint? workflow,
            string? modelOverride,
            int? requestedContextTokens,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static LocalJobResult<T> Result<T>(T value) =>
            new(
                value,
                new LocalUsageReceipt(
                    Guid.NewGuid(),
                    "local-lm",
                    "control",
                    "n/a",
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    0,
                    0,
                    null,
                    null,
                    null));
    }
}
