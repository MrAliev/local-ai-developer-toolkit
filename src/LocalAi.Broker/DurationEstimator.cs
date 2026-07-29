using LocalAi.Contracts;

namespace LocalAi.Broker;

public enum LocalSizeBucket
{
    Empty,
    Small,
    Medium,
    Large
}

public enum LocalCountBucket
{
    None,
    One,
    Few,
    Many
}

public enum LocalImageBucket
{
    None,
    Small,
    Medium,
    Large
}

public sealed record DurationObservationKey(
    LocalTaskProfile Profile,
    string Model,
    LocalSizeBucket Input,
    LocalSizeBucket Output,
    LocalCountBucket Files,
    LocalCountBucket Images,
    LocalImageBucket ImageResolution,
    bool IsCold,
    LocalDurationClass DurationClass);

public sealed record DurationPrediction(TimeSpan Median, TimeSpan P90, int Samples);

public sealed class DurationEstimator
{
    private const int MaxSamples = 101;
    private readonly Dictionary<DurationObservationKey, Queue<TimeSpan>> _samples = [];

    public IReadOnlyCollection<DurationObservationKey> Keys =>
        Array.AsReadOnly(_samples.Keys.ToArray());

    public void Observe(DurationObservationKey key, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (!_samples.TryGetValue(key, out var samples))
        {
            samples = new Queue<TimeSpan>();
            _samples.Add(key, samples);
        }

        samples.Enqueue(duration);
        while (samples.Count > MaxSamples)
        {
            samples.Dequeue();
        }
    }

    public DurationPrediction Predict(DurationObservationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_samples.TryGetValue(key, out var samples) || samples.Count == 0)
        {
            return key.DurationClass switch
            {
                LocalDurationClass.Short =>
                    new DurationPrediction(
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(10),
                        0),
                LocalDurationClass.Medium =>
                    new DurationPrediction(
                        TimeSpan.FromSeconds(15),
                        TimeSpan.FromSeconds(30),
                        0),
                LocalDurationClass.Long =>
                    new DurationPrediction(
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(2),
                        0),
                _ => throw new ArgumentOutOfRangeException(nameof(key))
            };
        }

        var ordered = samples.Order().ToArray();
        var median = ordered.Length % 2 == 0
            ? TimeSpan.FromTicks(
                (ordered[(ordered.Length / 2) - 1].Ticks +
                 ordered[ordered.Length / 2].Ticks) / 2)
            : ordered[ordered.Length / 2];
        var p90Index = Math.Max(
            0,
            (int)Math.Ceiling(ordered.Length * 0.90) - 1);
        return new DurationPrediction(median, ordered[p90Index], ordered.Length);
    }
}
