namespace LocalAi.Broker;

public enum BackendLiveness
{
    Healthy,
    Unhealthy,
    Inconclusive
}

public sealed record BackendProbeResult(
    BackendLiveness Liveness,
    string Code)
{
    public static BackendProbeResult Healthy(string code = "healthy") =>
        new(BackendLiveness.Healthy, code);

    public static BackendProbeResult Unhealthy(string code) =>
        new(BackendLiveness.Unhealthy, code);

    public static BackendProbeResult Inconclusive(string code) =>
        new(BackendLiveness.Inconclusive, code);
}

public sealed record BackendWatchdogPolicy(
    TimeSpan SilenceBeforeProbe,
    TimeSpan ProbeInterval,
    TimeSpan ProbeTimeout,
    int RequiredUnhealthyProbes)
{
    public static BackendWatchdogPolicy Default { get; } = new(
        SilenceBeforeProbe: TimeSpan.FromMinutes(10),
        ProbeInterval: TimeSpan.FromMinutes(1),
        ProbeTimeout: TimeSpan.FromSeconds(10),
        RequiredUnhealthyProbes: 2);

    public void Validate()
    {
        if (SilenceBeforeProbe <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SilenceBeforeProbe));
        }

        if (ProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProbeInterval));
        }

        if (ProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
        }

        if (RequiredUnhealthyProbes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RequiredUnhealthyProbes));
        }
    }
}

public sealed class BackendUnavailableException(string code)
    : Exception($"The model backend failed its liveness probe: {code}.")
{
    public string Code { get; } = code;
}
