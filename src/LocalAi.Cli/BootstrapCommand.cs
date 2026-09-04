using LocalAi.Cli.Resources;
using LocalAi.Contracts;

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
                [CliText.BootstrapAlreadyConnected]);
        }

        return new BootstrapPlan(
            repository,
            clients,
            [
                CliText.BootstrapStepState(repository.Identity.Id),
                CliText.BootstrapStepInitializing,
                CliText.BootstrapStepHooks,
                CliText.BootstrapStepClients,
                CliText.BootstrapStepModels,
                CliText.BootstrapStepLegacyKept
            ]);
    }
}
