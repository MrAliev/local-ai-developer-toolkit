using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Indexing;

/// <summary>
/// Maps a working directory to the repository it belongs to, and to that repository's index file.
/// </summary>
public static class RepoLocator
{
    /// <summary>
    /// The checkout the caller is actually working in - a worktree resolves to ITSELF, not to the
    /// main repository. This is where code is read from and where that branch's overlay lives.
    /// </summary>
    public static string ResolveWorkingRoot(string? candidate = null)
    {
        var start = Normalize(candidate);

        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            // A linked worktree has .git as a FILE pointing at the main repo's gitdir; the main
            // checkout has it as a directory. Either one marks a working root.
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName.TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        return start;
    }

    /// <summary>
    /// Where a working root keeps its overlay index. Inside the worktree on purpose: deleting the
    /// worktree takes the overlay with it, so stale overlays cannot accumulate. Requires
    /// <c>.claude/</c> to be git-ignored, or a multi-megabyte binary lands in someone's commit.
    /// </summary>
    public static string OverlayPathFor(string workingRoot) =>
        Path.Combine(workingRoot, ".claude", "codesearch", "overlay.cidx");

    /// <summary>
    /// Resolves the repository root for <paramref name="candidate"/> (or the current directory).
    ///
    /// A git worktree resolves to its MAIN repository, not to itself: this identifies the
    /// repository as a whole, and is what the single shared BASE index is keyed by. Per-branch
    /// differences live in overlays instead of in separate full indexes.
    /// </summary>
    public static string ResolveRoot(string? candidate = null)
    {
        var start = Normalize(candidate);
        var fromGit = TryGitCommonRoot(start);
        if (fromGit is not null)
        {
            return fromGit;
        }

        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName.TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        return start.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Index location for a repository. The path hash keeps two checkouts of the same-named repo
    /// (say R:\IntelWash and R:\OldRepo\IntelWash_old) from colliding on one file.
    /// </summary>
    public static string IndexPathFor(string root)
    {
        try
        {
            return RuntimeIndexLayout.ResolveBaseIndexPath(root);
        }
        catch (InvalidOperationException)
        {
            return LegacyIndexPathFor(root);
        }
    }

    public static string LegacyIndexPathFor(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name))
        {
            name = full.Replace(":", string.Empty).Replace(Path.DirectorySeparatorChar, '-');
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8].ToLowerInvariant();

        return Path.Combine(IndexDirectory, $"{name}-{hash}.cidx");
    }

    public static string IndexDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi", "repositories", "legacy");

    public static string GitCommit(string root) => RunGit(root, "rev-parse HEAD") ?? string.Empty;

    public static string? GitOutput(string workingDirectory, string arguments) =>
        RunGit(workingDirectory, arguments);

    public static byte[]? GitOutputBytes(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            Task.WaitAll([copy, stderr], GitTimeoutMs);
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Normalize(string? candidate)
    {
        var start = string.IsNullOrWhiteSpace(candidate)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(candidate);

        if (File.Exists(start))
        {
            start = Path.GetDirectoryName(start)!;
        }

        return start.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string? TryGitCommonRoot(string start)
    {
        // --git-common-dir points at the MAIN repo's .git even when run from a linked worktree,
        // which is exactly the redirection we want.
        var commonDir = RunGit(start, "rev-parse --path-format=absolute --git-common-dir");
        if (string.IsNullOrWhiteSpace(commonDir))
        {
            return null;
        }

        var gitDir = new DirectoryInfo(commonDir.Replace('/', Path.DirectorySeparatorChar));
        var parent = gitDir.Parent;
        return parent?.FullName.TrimEnd(Path.DirectorySeparatorChar);
    }

    private const int GitTimeoutMs = 10_000;

    /// <summary>
    /// Runs git and returns its trimmed stdout, or null for any failure.
    ///
    /// Every detail here is load-bearing, because the naive version deadlocked this process
    /// permanently when the MCP server ran with its own stdio redirected:
    /// <list type="number">
    /// <item><b>stdin is redirected and closed immediately.</b> Otherwise the child inherits the
    /// server's stdin pipe, which stays open for the life of the session, and any git that reads
    /// from it waits forever.</item>
    /// <item><b>stderr is drained.</b> An unread stderr pipe fills at ~4KB and blocks the child
    /// mid-write, so it never exits.</item>
    /// <item><b>WaitForExit comes before reading to the end.</b> The old order blocked in
    /// ReadToEnd first, so the timeout could never fire - a hung git hung the whole server.</item>
    /// </list>
    /// </summary>
    private static string? RunGit(string workingDirectory, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(GitTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already gone, or not ours to kill - nothing useful to do either way.
                }

                return null;
            }

            // WaitForExit(int) returns once the process ends but does not await the redirected
            // readers, so the buffers still have to be drained before reading the result.
            if (!Task.WaitAll([stdout, stderr], GitTimeoutMs))
            {
                return null;
            }

            var output = stdout.Result.Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
