using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelTelemetryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-telemetry-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Prompt")]
    [InlineData("Answer")]
    [InlineData("Content")]
    [InlineData("Image")]
    [InlineData("Path")]
    [InlineData("Secret")]
    public void Content_free_guard_rejects_sensitive_member_names(string member)
    {
        var value = new Dictionary<string, object?> { [member] = "sensitive" };

        Assert.Throws<InvalidOperationException>(
            () => ModelTelemetryStore.EnsureContentFree(value));
    }

    [Fact]
    public async Task Telemetry_persists_only_buckets_timings_outcomes_and_token_estimates()
    {
        var store = new ModelTelemetryStore(_root);
        var record = new ModelTelemetryRecord(
            Guid.NewGuid(),
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            2048,
            LocalSizeBucket.Small,
            LocalSizeBucket.Small,
            WasCold: true,
            ModelSwitched: true,
            UsedFallback: false,
            "structure:pass",
            ModelExecutionOutcome.Success,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(60),
            100,
            10,
            90,
            "1",
            new DateTimeOffset(2026, 7, 29, 1, 0, 0, TimeSpan.Zero));

        await store.AppendAsync(record, TestContext.Current.CancellationToken);

        var loaded = Assert.Single(
            await store.ReadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(record, loaded);
        var json = await File.ReadAllTextAsync(
            Assert.Single(Directory.GetFiles(store.MetricsDirectory, "*.json")),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Experiment_task_telemetry_is_trimmed_by_the_write_that_supersedes_it()
    {
        var store = new ModelTelemetryStore(_root);
        var old = ExperimentTask(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddDays(-8));
        var current = ExperimentTask(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);
        await store.AppendExperimentTaskAsync(
            old,
            TestContext.Current.CancellationToken);
        await store.AppendExperimentTaskAsync(
            current,
            TestContext.Current.CancellationToken);

        var records = await store.ReadExperimentTasksAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(current.WorkflowId, Assert.Single(records).WorkflowId);
        Assert.False(
            File.Exists(
                Path.Combine(
                    store.ExperimentTasksDirectory,
                    $"{old.WorkflowId:N}.json")));
    }

    [Fact]
    public async Task Reading_experiment_task_telemetry_does_not_discard_it()
    {
        var store = new ModelTelemetryStore(_root);
        var old = ExperimentTask(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddDays(-30));
        await store.AppendExperimentTaskAsync(
            old,
            TestContext.Current.CancellationToken);

        // Read it twice. The report is a getter, and a getter that deletes what it reports on
        // means the history shrinks every time anybody looks at it — which is how a pair with
        // six completed attempts came to report one.
        await store.ReadExperimentTasksAsync(TestContext.Current.CancellationToken);
        var records = await store.ReadExperimentTasksAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(old.WorkflowId, Assert.Single(records).WorkflowId);
        Assert.True(
            File.Exists(
                Path.Combine(
                    store.ExperimentTasksDirectory,
                    $"{old.WorkflowId:N}.json")));
    }

    private static ExperimentTaskTelemetryRecord ExperimentTask(
        Guid workflowId,
        DateTimeOffset recordedAtUtc) =>
        new(
            workflowId,
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ModelExecutionOutcome.Success,
            TimeSpan.FromSeconds(1),
            ColdExecutions: 0,
            WarmExecutions: 1,
            UsedFallback: false,
            InputTokens: 100,
            OutputTokens: 100,
            LocalTokensProcessed: 200,
            EstimatedCloudGenerationTokensSaved: 100,
            EstimatedNetCloudContextTokensSaved: 0,
            CatalogVersion: "1",
            recordedAtUtc);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
