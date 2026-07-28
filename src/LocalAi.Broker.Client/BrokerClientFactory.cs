namespace LocalAi.Broker.Client;

public static class BrokerClientFactory
{
    public static BrokerClient CreateDefault(string? runtimeRoot = null)
    {
        var root = runtimeRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAi");
        new RuntimeAcl().Ensure(root);
        return new BrokerClient(
            new DurableQueue(root),
            BrokerProcess.CreateDefault(root));
    }
}
