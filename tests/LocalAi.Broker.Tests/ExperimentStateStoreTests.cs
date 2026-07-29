using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ExperimentStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-experiments-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Store_round_trips_content_free_per_profile_state_atomically()
    {
        var store = new ExperimentStateStore(_root);
        var state = ExperimentSnapshot.Empty
            .Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success)
            .Record(
                LocalTaskProfile.TechnicalTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.StructuralFailure);

        await store.SaveAsync(state, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            1,
            loaded.Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b").CompletedAttempts);
        Assert.Equal(
            1,
            loaded.Pair(
                LocalTaskProfile.TechnicalTranslation,
                "translategemma:12b").StructuralFailures);
        var json = await File.ReadAllTextAsync(
            store.StatePath,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.StatePath)!, "*.tmp"));
    }

    [Theory]
    [InlineData(ExperimentOwnerAction.Promote)]
    [InlineData(ExperimentOwnerAction.ContinueExperiment)]
    [InlineData(ExperimentOwnerAction.FallbackOnly)]
    [InlineData(ExperimentOwnerAction.Disable)]
    public async Task Owner_feedback_persists_the_exact_action(
        ExperimentOwnerAction action)
    {
        var store = new ExperimentStateStore(_root);
        var state = ExperimentSnapshot.Empty.ApplyFeedback(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            action);

        await store.SaveAsync(state, TestContext.Current.CancellationToken);
        var pair = (await store.LoadAsync(TestContext.Current.CancellationToken))
            .Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b");

        Assert.Equal(action, pair.OwnerAction);
    }

    [Fact]
    public void Continue_experiment_starts_a_fresh_ten_task_batch()
    {
        var firstWorkflow = Guid.NewGuid();
        var secondWorkflow = Guid.NewGuid();
        var state = ExperimentSnapshot.Empty
            .RecordWorkflow(
                firstWorkflow,
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success)
            .RecordWorkflow(
                secondWorkflow,
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.StructuralFailure);

        var continued = state.ApplyFeedback(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ExperimentOwnerAction.ContinueExperiment);

        var pair = continued.Pair(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b");
        Assert.Equal(0, pair.CompletedAttempts);
        Assert.Equal(0, pair.Successes);
        Assert.Equal(0, pair.StructuralFailures);
        Assert.Empty(pair.CompletedWorkflows!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
