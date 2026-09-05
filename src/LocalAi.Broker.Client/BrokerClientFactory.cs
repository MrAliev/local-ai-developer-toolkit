using LocalAi.Contracts;

namespace LocalAi.Broker.Client;

public static class BrokerClientFactory
{
    public static BrokerClient CreateDefault(
        string? runtimeRoot = null,
        ILocalRunObserver? observer = null)
    {
        var root = runtimeRoot ?? ModelResidencyPolicyStore.DefaultRuntimeRoot;
        new RuntimeAcl().Ensure(root);
        return new BrokerClient(
            new DurableQueue(root),
            BrokerProcess.CreateDefault(root),
            observer: observer);
    }
}
