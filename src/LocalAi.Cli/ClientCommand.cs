using LocalAi.Contracts;

namespace LocalAi.Cli;

public static class ClientCommand
{
    public static ClientRegistrationPlan Plan(string installationDirectory) =>
        ClientCommandPlan.Plan(installationDirectory);

    public static IReadOnlyList<string> McpFallbackChoices() =>
        ClientCommandPlan.McpFallbackChoices();
}
