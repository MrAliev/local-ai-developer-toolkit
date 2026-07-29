using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class DurationEstimatorTests
{
    [Fact]
    public void Unknown_work_uses_conservative_duration_classes()
    {
        var estimator = new DurationEstimator();
        var key = Key("model", LocalDurationClass.Short);

        Assert.Equal(TimeSpan.FromSeconds(5), estimator.Predict(key).Median);
        Assert.Equal(TimeSpan.FromSeconds(10), estimator.Predict(key).P90);
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            estimator.Predict(key with { DurationClass = LocalDurationClass.Long }).P90);
    }

    [Fact]
    public void Observations_produce_rolling_median_and_p90_without_content()
    {
        var estimator = new DurationEstimator();
        var key = Key("model", LocalDurationClass.Medium);
        foreach (var seconds in Enumerable.Range(1, 10))
        {
            estimator.Observe(key, TimeSpan.FromSeconds(seconds));
        }

        var prediction = estimator.Predict(key);

        Assert.Equal(TimeSpan.FromSeconds(5.5), prediction.Median);
        Assert.Equal(TimeSpan.FromSeconds(9), prediction.P90);
        Assert.DoesNotContain(
            estimator.Keys,
            value => value.ToString()!.Contains("prompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Schedule_metadata_learns_from_completed_routed_work()
    {
        var estimator = new DurationEstimator();
        var resolver = new ScheduleMetadataResolver(
            ModelRoutingCatalog.LoadEmbedded(),
            estimator);
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "duration-observation",
            LocalJobPriority.Foreground,
            LocalTaskProfile.PlainTranslation,
            "translate",
            null,
            [],
            new LocalWorkloadMetadata(
                100,
                100,
                1,
                0,
                0,
                LocalDurationClass.Short));
        var candidate = new QueuedJobCandidate(
            request,
            Sequence: 1,
            request.CreatedAtUtc);
        var routing = new LocalRoutingReceipt(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            2048,
            WasCold: false,
            UsedFallback: false,
            "structure:pass",
            10,
            1,
            9,
            IsExperimentalAttempt: true);

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            resolver.Resolve(
                candidate,
                routing.SelectedModel,
                residentModel: routing.SelectedModel).PredictedDuration);

        resolver.Observe(request, routing, TimeSpan.FromSeconds(3));

        Assert.Equal(
            TimeSpan.FromSeconds(3),
            resolver.Resolve(
                candidate,
                routing.SelectedModel,
                residentModel: routing.SelectedModel).PredictedDuration);
    }

    private static DurationObservationKey Key(
        string model,
        LocalDurationClass durationClass) =>
        new(
            LocalTaskProfile.CodeAnalysis,
            model,
            LocalSizeBucket.Small,
            LocalSizeBucket.Small,
            LocalCountBucket.One,
            LocalCountBucket.None,
            LocalImageBucket.None,
            IsCold: false,
            durationClass);
}
