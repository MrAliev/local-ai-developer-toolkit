namespace LocalAi.Cli;

public sealed record BootstrapPlan(
    RepositoryStatus Repository,
    ClientRegistrationPlan Clients,
    IReadOnlyList<string> Changes);

public static class BootstrapCommand
{
    public static BootstrapPlan Plan(
        string commonDirectory,
        string runtimeRoot,
        string installationDirectory)
    {
        var repository = RepoCommand.Status(commonDirectory, runtimeRoot);
        var clients = ClientCommand.Plan(installationDirectory);
        if (repository.Configured)
        {
            return new BootstrapPlan(
                repository,
                clients,
                ["Repository is already configured; no bootstrap changes are planned."]);
        }

        return new BootstrapPlan(
            repository,
            clients,
            [
                $"Create LocalAi repository state for {repository.Identity.Id}.",
                "Set repository state to INITIALIZING until a verified mainline generation exists.",
                "Install a chained LocalAi Git hook dispatcher after compatibility checks.",
                "Register CodeSearch and LocalLm through the stable launcher in Claude and Codex.",
                "Synchronize recommended models through the local_models_sync MCP tool.",
                "Do not remove legacy models or profiles."
            ]);
    }

    public static void RequireAcceptance(bool accept)
    {
        if (!accept)
        {
            throw new InvalidOperationException(
                "Bootstrap is dry-run only until --accept is explicitly supplied.");
        }
    }
}
