using CodeSearch.Core.Chunking;

namespace CodeSearch.Core.Indexing;

public static class FileScanner
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".vscode",
        "node_modules", "packages", "TestResults", "coverage",
        "dist", "build", "out", ".next", ".nuxt", ".angular",
        ".venv", "venv", "__pycache__", ".pytest_cache",
        ".terraform", "artifacts",
    };

    /// <summary>
    /// Enumerates indexable files under <paramref name="root"/>, returning paths relative to it.
    ///
    /// Worktrees are excluded explicitly: <c>.claude\worktrees</c> holds full checkouts of the
    /// same solution, so indexing them multiplies every file by the number of live worktrees and
    /// fills results with near-identical hits from other branches.
    /// </summary>
    public static List<string> Enumerate(string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var gitFiles = RepoLocator.GitOutputBytes(
            rootFull,
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]);
        if (gitFiles is not null)
        {
            return System.Text.Encoding.UTF8.GetString(gitFiles)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace('/', Path.DirectorySeparatorChar))
                .Where(path => ChunkerFactory.IsIndexable(path))
                .Where(path => IsSafeIndexableFile(rootFull, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var results = new List<string>();
        Walk(rootFull, rootFull, results);
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static bool IsSafeIndexableFile(string rootFull, string relativePath) =>
        SafeSourcePath.TryResolveFile(
            rootFull,
            relativePath,
            out var fullPath,
            out _) &&
        IsWithinSizeLimit(fullPath);

    private static bool IsWithinSizeLimit(string path)
    {
        try
        {
            return File.Exists(path) &&
                   new FileInfo(path).Length <= ChunkLimits.MaxFileBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void Walk(string rootFull, string directory, List<string> results)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var relativePath = Path.GetRelativePath(rootFull, entry);
            if (!SafeSourcePath.TryResolveExisting(
                    rootFull,
                    relativePath,
                    out var safeEntry,
                    out _))
            {
                continue;
            }

            if (Directory.Exists(safeEntry))
            {
                var name = Path.GetFileName(safeEntry);
                if (ExcludedDirectories.Contains(name))
                {
                    continue;
                }

                if (name.Equals("worktrees", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(Path.GetDirectoryName(safeEntry))?.Equals(".claude", StringComparison.OrdinalIgnoreCase) == true)
                {
                    continue;
                }

                Walk(rootFull, safeEntry, results);
                continue;
            }

            if (!ChunkerFactory.IsIndexable(relativePath))
            {
                continue;
            }

            try
            {
                if (new FileInfo(safeEntry).Length > ChunkLimits.MaxFileBytes)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            results.Add(relativePath);
        }
    }
}
