using System.Text.Json.Serialization;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
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

/// <summary>
/// What <c>repo status --json</c> tells a program: which repository this is about, and the
/// verdict as the token the prose already prints.
///
/// The prose does not travel with it. The sentence after the token is an instruction to an
/// agent, it is reworded whenever it turns out to be wrong, and freezing it into a versioned
/// contract would make the next rewording a wire change.
/// </summary>
public sealed record RepoStatusData(
    [property: JsonRequired, JsonPropertyName("repositoryId"), JsonPropertyOrder(0)]
    string RepositoryId,
    [property: JsonRequired, JsonPropertyName("commonDirectory"), JsonPropertyOrder(1)]
    string CommonDirectory,
    [property: JsonRequired, JsonPropertyName("status"), JsonPropertyOrder(2)]
    string Status);

public static class RepoCommand
{
    /// <summary>
    /// The two verdicts, as tokens. Named here because the prose and the machine face have to
    /// print the same word: documents, tests and the instruction block all match on it.
    /// </summary>
    public const string Configured = "CONFIGURED";

    /// <inheritdoc cref="Configured"/>
    public const string NotConfigured = "NOT_CONFIGURED";

    /// <summary>
    /// Not a boolean. INITIALIZING is a real state in this product — a connected repository
    /// whose first generation is still building — and the day it reaches this command a boolean
    /// is a breaking change while a token is an added member.
    /// </summary>
    public static RepoStatusData MachineStatus(RepositoryStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new RepoStatusData(
            status.Identity.Id,
            status.Identity.CommonDirectory,
            status.Configured ? Configured : NotConfigured);
    }

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
        out CommandRefusal? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        target = new RepoStatusTarget(null, ResolveThroughGit: true);
        refusal = null;
        string? path = null;
        var resolveThroughGit = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--root")
            {
                if (index + 1 >= arguments.Count)
                {
                    refusal = new CommandRefusal(
                        "root_value_missing",
                        CliText.RepoStatusRootWithoutDirectory);
                    return false;
                }

                if (path is not null)
                {
                    refusal = new CommandRefusal(
                        "repository_ambiguous",
                        CliText.RepoStatusTwoRepositories);
                    return false;
                }

                path = arguments[++index];
                resolveThroughGit = true;
                continue;
            }

            if (argument.StartsWith('-'))
            {
                refusal = new CommandRefusal(
                    "argument_unknown",
                    CliText.RepoStatusUnknownArgument(argument, CliUsage.RepoStatus));
                return false;
            }

            if (path is not null)
            {
                refusal = new CommandRefusal(
                    "repository_ambiguous",
                    CliText.RepoStatusTwoRepositories);
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
        var configured = new RepositoryManifestStore(FsPath.From(repositoryRoot)).Read() is not null;

        // The verdict names the repository it is about. A caller reaches this command with a
        // path, a worktree or nothing at all, and every one of those resolves to a common
        // directory that may not be the one they had in mind — which is how #94 was found. The
        // token stays first so anything matching on it keeps working.
        var message = configured
            ? $"{Configured}: {identity.CommonDirectory}"
            : CliText.RepoStatusNotConfigured(identity.CommonDirectory);
        return new RepositoryStatus(identity, configured, message);
    }
}
