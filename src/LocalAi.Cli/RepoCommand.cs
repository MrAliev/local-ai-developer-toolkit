using LocalAi.Repository;

namespace LocalAi.Cli;

public sealed record RepositoryStatus(
    RepositoryIdentity Identity,
    bool Configured,
    string Message);

public static class RepoCommand
{
    public static RepositoryStatus Status(
        string commonDirectory,
        string localAiRuntimeRoot)
    {
        var identity = RepositoryIdentity.FromCommonDirectory(commonDirectory);
        var repositoryRoot = Path.Combine(
            Path.GetFullPath(localAiRuntimeRoot),
            "repositories",
            identity.Id);
        var configured = new RepositoryManifestStore(repositoryRoot).Read() is not null;
        var message = configured
            ? "CONFIGURED"
            : "NOT_CONFIGURED: offer CodeSearch, LocalLm, shared broker, " +
              "mainline generations, branch overlays, hooks, Claude MCP and Codex MCP.";
        return new RepositoryStatus(identity, configured, message);
    }
}
