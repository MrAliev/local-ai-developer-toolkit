using System.Security.Cryptography;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace CodeSearch.Core.Indexing;

public sealed record WorkingIndexIdentity(
    string WorkingRoot,
    string RepositoryRoot,
    string RepositoryId,
    string RepositoryRuntimeRoot,
    string HeadCommit,
    string HeadTree,
    string? DirtyHash);

public static class RuntimeIndexLayout
{
    /// <summary>
    /// The installation directory used when a caller does not name one. Deferred to the one
    /// definition every LocalAi component already shares rather than spelled out again here:
    /// two answers to "which installation" is the problem this parameter exists to remove.
    /// </summary>
    public static string DefaultRuntimeRoot => ModelResidencyPolicyStore.DefaultRuntimeRoot;

    /// <summary>
    /// Inspects a working tree against a runtime root.
    ///
    /// <paramref name="runtimeRoot"/> exists so a caller can say which installation to look at
    /// rather than always being handed the machine's own. Tests are the pressing case: they
    /// wrote into the real %LOCALAPPDATA%\LocalAi and shared its broker, so an unrelated index
    /// build running at the same moment could fail them — which reads as a flaky test rather
    /// than as two things using one directory.
    /// </summary>
    public static WorkingIndexIdentity Inspect(
        string workingRoot,
        string? runtimeRoot = null)
    {
        workingRoot = RepoLocator.ResolveWorkingRoot(workingRoot);
        var repositoryRoot = RepoLocator.ResolveRoot(workingRoot);
        var commonDirectory = RepoLocator.GitOutputOrThrow(
            workingRoot,
            "rev-parse --path-format=absolute --git-common-dir",
            "The git common directory");
        var identity = RepositoryIdentity.FromCommonDirectory(commonDirectory);
        var repositoryRuntimeRoot = Path.Combine(
            string.IsNullOrWhiteSpace(runtimeRoot) ? DefaultRuntimeRoot : runtimeRoot,
            "repositories",
            identity.Id);
        var head = RepoLocator.GitOutputOrThrow(
            workingRoot,
            "rev-parse HEAD",
            "Git HEAD");
        var tree = RepoLocator.GitOutputOrThrow(
            workingRoot,
            "rev-parse HEAD^{tree}",
            "The git HEAD tree");
        var dirtyPaths = GetDirtyPaths(workingRoot);
        var dirtyHash = dirtyPaths.Count == 0
            ? null
            : DirtyCorpusPolicy.ComputeWorkingContentHash(workingRoot, dirtyPaths);

        return new WorkingIndexIdentity(
            workingRoot,
            repositoryRoot,
            identity.Id,
            repositoryRuntimeRoot,
            head,
            tree,
            dirtyHash);
    }

    public static string ResolveBaseIndexPath(
        string repositoryRoot,
        string? runtimeRoot = null)
    {
        var identity = Inspect(repositoryRoot, runtimeRoot);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        var current = store.ReadCurrent();
        return current is null
            ? RepoLocator.LegacyIndexPathFor(identity.RepositoryRoot, runtimeRoot)
            : store.IndexPath(current.GenerationId);
    }

    public static string OverlayPath(
        WorkingIndexIdentity identity,
        string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        var worktreeId = WorktreeKey(identity.WorkingRoot);
        return Path.Combine(
            identity.RepositoryRuntimeRoot,
            "overlays",
            generationId,
            worktreeId,
            identity.HeadTree,
            (identity.DirtyHash ?? "clean") + ".cidx");
    }

    public static string SemanticOverlayPath(
        WorkingIndexIdentity identity,
        string generationId) =>
        Path.ChangeExtension(OverlayPath(identity, generationId), ".sidx");

    public static List<string> GetDirtyPaths(string workingRoot)
    {
        workingRoot = RepoLocator.ResolveWorkingRoot(workingRoot);
        var changed = RepoLocator.GitOutputBytes(
            workingRoot,
            ["diff", "--name-only", "-z", "HEAD", "--"]);
        var untracked = RepoLocator.GitOutputBytes(
            workingRoot,
            ["ls-files", "--others", "--exclude-standard", "-z"]);
        return ParseNullPaths(changed)
            .Concat(ParseNullPaths(untracked))
            .Where(path => DirtyCorpusPolicy.IsAllowed(path, gitIgnored: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ParseNullPaths(byte[]? output)
    {
        if (output is null || output.Length == 0)
        {
            return [];
        }

        return Encoding.UTF8.GetString(output)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The directory name an overlay for this worktree lives under. Exposed because retention
    /// has to recognise the same key rather than compute its own version of it.
    ///
    /// Takes an <see cref="FsPath"/> so the normalisation cannot be skipped: git prints forward
    /// slashes on Windows and Inspect yields backslashes, so the same worktree hashed to two
    /// different keys depending on who asked — and a key that matches no directory reads as
    /// "this worktree is gone", which is how live overlays came to be deleted.
    /// </summary>
    public static string WorktreeKey(FsPath workingRoot) => Hash(workingRoot.IdentityKey);

    /// <summary>For a working root arriving as text — a command-line argument, git output.</summary>
    public static string WorktreeKey(string workingRoot) => WorktreeKey(FsPath.From(workingRoot));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
