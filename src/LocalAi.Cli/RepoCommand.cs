using LocalAi.Repository;

namespace LocalAi.Cli;

public sealed record RepositoryStatus(
    RepositoryIdentity Identity,
    bool Configured,
    string Message);

/// <summary>
/// What <c>repo status</c> was asked about: a path, and whether it is a directory inside the
/// repository that Git has to resolve, or the common directory itself.
/// </summary>
public sealed record RepoStatusTarget(string? Path, bool ResolveThroughGit);

public static class RepoCommand
{
    /// <summary>
    /// Reads the arguments of <c>repo status</c>, rejecting anything it does not understand.
    /// </summary>
    /// <remarks>
    /// This used to take <c>args[2]</c> as the common directory whatever it was. Every other
    /// command in this CLI locates a repository with <c>--root</c>, so that is what anyone
    /// reaches for — and <c>repo status --root C:\repo</c> hashed the literal string
    /// <c>--root</c> into a repository id that had never been configured and answered
    /// NOT_CONFIGURED about a repository that was. The instruction block installed into
    /// `CLAUDE.md` and `AGENTS.md` tells agents to run this command when they open a
    /// repository, so the wrong answer arrived with an offer to set everything up again.
    /// </remarks>
    public static bool TryParseStatusArguments(
        IReadOnlyList<string> arguments,
        out RepoStatusTarget target,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        target = new RepoStatusTarget(null, ResolveThroughGit: true);
        error = null;
        string? path = null;
        var resolveThroughGit = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--root")
            {
                if (index + 1 >= arguments.Count)
                {
                    error = "localai repo status --root requires a directory.";
                    return false;
                }

                if (path is not null)
                {
                    error = "localai repo status accepts one repository, not two.";
                    return false;
                }

                path = arguments[++index];
                resolveThroughGit = true;
                continue;
            }

            if (argument.StartsWith('-'))
            {
                error = $"localai repo status does not understand '{argument}'.";
                return false;
            }

            if (path is not null)
            {
                error = "localai repo status accepts one repository, not two.";
                return false;
            }

            path = argument;
            resolveThroughGit = false;
        }

        target = new RepoStatusTarget(path, resolveThroughGit);
        return true;
    }

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
