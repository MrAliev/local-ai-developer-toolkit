namespace LocalAi.Broker.Client;

public sealed class BrokerBootstrapException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal interface IBrokerStartAttempt : IDisposable
{
    int ProcessId { get; }

    bool TryGetExitCode(out int exitCode);
}

internal enum BrokerObservationStatus
{
    CompatibleHealthy,
    IncompatibleHealthy,
    AbsentOrStale,
    StartingOrLockOwned
}

internal sealed record BrokerObservation(
    BrokerObservationStatus Status,
    string Detail);
