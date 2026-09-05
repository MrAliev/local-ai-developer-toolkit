using LocalAi.Contracts;

namespace LocalAi.Broker.Client;

public static class BrokerClientFactory
{
    /// <summary>
    /// <paramref name="deadline"/> is the caller's runaway guard, not an expectation. It
    /// exists because the default is thirty minutes, which is right for a call to a model
    /// and wrong for a download: a twelve-gigabyte model on an ordinary connection takes
    /// longer than that, and the console used to give up and report a cancellation while
    /// the broker went on downloading.
    /// </summary>
    public static BrokerClient CreateDefault(
        string? runtimeRoot = null,
        ILocalRunObserver? observer = null,
        TimeSpan? deadline = null)
    {
        var root = runtimeRoot ?? ModelResidencyPolicyStore.DefaultRuntimeRoot;
        new RuntimeAcl().Ensure(root);
        return new BrokerClient(
            new DurableQueue(root),
            BrokerProcess.CreateDefault(root),
            timeout: deadline,
            observer: observer);
    }
}
