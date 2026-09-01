using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

/// <summary>
/// What a repository's retention pass removed, or would remove.
/// </summary>
public sealed record GenerationRetentionResult(
    IReadOnlyList<string> GenerationsRemoved,
    IReadOnlyList<string> OverlaysRemoved,
    IReadOnlyList<string> StagingRemoved,
    long BytesReclaimed)
{
    public static GenerationRetentionResult Empty { get; } = new([], [], [], 0);

    public int ActionCount =>
        GenerationsRemoved.Count + OverlaysRemoved.Count + StagingRemoved.Count;
}

/// <summary>
/// Bounds the disk a single repository's index occupies.
///
/// A generation is immutable and keyed by the mainline tree, so every indexed commit leaves one
/// behind — half a gigabyte each, for a repository whose working index is one of them. Keeping a
/// couple is worth real time: a branch that returns to a recent base reuses its generation instead
/// of re-embedding the corpus. Keeping twenty-two is only a bill.
///
/// Overlays are keyed by generation, so an overlay whose generation is gone can never be selected
/// again; those go with it rather than waiting for a bound of their own.
///
/// The generation named by <c>current.json</c> is never removed, whatever the count says. Neither
/// is anything a running index build is still staging.
/// </summary>
public static class GenerationRetention
{
    /// <summary>
    /// <paramref name="reachable"/> is what the repository can still ask about: for each live
    /// worktree, the hash of its path and the tree its HEAD points at. Everything else under
    /// a kept generation is an overlay for a commit nobody is on any more, or for a worktree
    /// that no longer exists — and nothing ever removed those, so weeks of branch work left
    /// one overlay per commit per worktree, six times the size of the generation they hang
    /// off. Pass null to leave overlays alone, which is what a caller that cannot enumerate
    /// worktrees must do rather than guess.
    /// </summary>
    public static GenerationRetentionResult Prune(
        string repositoryRuntimeRoot,
        RuntimeRetentionPolicy policy,
        DateTimeOffset now,
        bool dryRun = false,
        IReadOnlySet<(string WorktreeId, string HeadTree)>? reachable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRuntimeRoot);
        ArgumentNullException.ThrowIfNull(policy);
        policy = policy.Normalized();
        var root = Path.GetFullPath(repositoryRuntimeRoot);
        var generationsRoot = Path.Combine(root, "generations");
        if (!Directory.Exists(generationsRoot))
        {
            return GenerationRetentionResult.Empty;
        }

        var store = new GenerationStore(root);
        string? current = null;
        try
        {
            current = store.ReadCurrent()?.GenerationId;
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or IOException or
                UnauthorizedAccessException)
        {
            // An unreadable pointer means nothing can be proved safe to remove. Removing the
            // wrong generation costs an hour of embedding; keeping them all costs disk.
            return GenerationRetentionResult.Empty;
        }

        var candidates = Directory.EnumerateDirectories(generationsRoot)
            .Select(directory => new
            {
                Directory = directory,
                Id = Path.GetFileName(directory),
                PublishedAtUtc = PublishedAt(store, directory),
            })
            .Where(candidate => !candidate.Directory.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.PublishedAtUtc)
            .ToArray();

        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (current is not null)
        {
            keep.Add(current);
        }

        foreach (var candidate in candidates)
        {
            if (keep.Count >= policy.GenerationsPerRepository)
            {
                break;
            }

            keep.Add(candidate.Id);
        }

        var generationsRemoved = new List<string>();
        var bytes = 0L;
        foreach (var candidate in candidates.Where(item => !keep.Contains(item.Id)))
        {
            var size = DirectorySize(candidate.Directory);
            if (!TryRemove(candidate.Directory, dryRun))
            {
                continue;
            }

            bytes += size;
            generationsRemoved.Add(candidate.Id);
        }

        var overlaysRemoved = new List<string>();
        var overlaysRoot = Path.Combine(root, "overlays");
        if (Directory.Exists(overlaysRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(overlaysRoot))
            {
                if (!keep.Contains(Path.GetFileName(directory)))
                {
                    // The generation is gone, so nothing under it can be read at all.
                    var size = DirectorySize(directory);
                    if (!TryRemove(directory, dryRun))
                    {
                        continue;
                    }

                    bytes += size;
                    overlaysRemoved.Add(Path.GetFileName(directory));
                    continue;
                }

                if (reachable is null)
                {
                    continue;
                }

                // The generation is kept, but most of what is under it is not reachable: one
                // directory per worktree, and inside it one per tree that worktree's HEAD has
                // ever pointed at.
                foreach (var worktree in Directory.EnumerateDirectories(directory))
                {
                    var worktreeId = Path.GetFileName(worktree);
                    var live = reachable.Where(entry => entry.WorktreeId == worktreeId).ToArray();
                    if (live.Length == 0)
                    {
                        var size = DirectorySize(worktree);
                        if (TryRemove(worktree, dryRun))
                        {
                            bytes += size;
                            overlaysRemoved.Add(Path.GetFileName(directory) + "/" + worktreeId);
                        }

                        continue;
                    }

                    foreach (var tree in Directory.EnumerateDirectories(worktree))
                    {
                        var headTree = Path.GetFileName(tree);
                        if (live.Any(entry => entry.HeadTree == headTree))
                        {
                            continue;
                        }

                        var size = DirectorySize(tree);
                        if (TryRemove(tree, dryRun))
                        {
                            bytes += size;
                            overlaysRemoved.Add(
                                Path.GetFileName(directory) + "/" + worktreeId + "/" + headTree);
                        }
                    }
                }
            }
        }

        var stagingRemoved = new List<string>();
        var stagingRoot = Path.Combine(root, "staging");
        if (Directory.Exists(stagingRoot))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(stagingRoot))
            {
                // A staging file belongs to an index build that may still be running. Only the
                // ones no build could plausibly still be holding are collected.
                if (now - File.GetLastWriteTimeUtc(entry) < TimeSpan.FromDays(1))
                {
                    continue;
                }

                var size = Directory.Exists(entry) ? DirectorySize(entry) : Length(entry);
                if (!TryRemove(entry, dryRun))
                {
                    continue;
                }

                bytes += size;
                stagingRemoved.Add(Path.GetFileName(entry));
            }
        }

        // Self-healing quarantines a corrupt progress file and moves on, which is right, but the
        // quarantined copy then outlives everything it could have explained.
        foreach (var quarantined in Directory.EnumerateFiles(root, "*.corrupt-*"))
        {
            if (now - File.GetLastWriteTimeUtc(quarantined) < policy.ArchiveRetention)
            {
                continue;
            }

            var size = Length(quarantined);
            if (!TryRemove(quarantined, dryRun))
            {
                continue;
            }

            bytes += size;
            stagingRemoved.Add(Path.GetFileName(quarantined));
        }

        return new GenerationRetentionResult(
            generationsRemoved,
            overlaysRemoved,
            stagingRemoved,
            bytes);
    }

    private static DateTimeOffset PublishedAt(GenerationStore store, string directory)
    {
        try
        {
            return store.ReadManifest(Path.GetFileName(directory)).PublishedAtUtc;
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or IOException or
                UnauthorizedAccessException or InvalidDataException)
        {
            // A generation whose manifest does not verify cannot be loaded by a search either, so
            // it is not worth protecting — but it is still ordered by when it appeared rather
            // than treated as brand new.
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Removes the path, or reports that it could not be.
    ///
    /// A generation currently mapped by a running search holds its index file open, and Windows
    /// answers that with a sharing violation rather than a delete. That is not an error worth
    /// failing a housekeeping pass over — the generation stays, the next pass tries again, and
    /// the caller does not get told it reclaimed space it did not.
    /// </summary>
    private static bool TryRemove(string path, bool dryRun)
    {
        if (dryRun)
        {
            return true;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(Length);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long Length(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
