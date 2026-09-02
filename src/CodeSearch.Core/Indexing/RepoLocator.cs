using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LocalAi.Contracts;

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
    public static FsPath ResolveWorkingRoot(string? candidate = null)
    {
        var start = Normalize(candidate);

        for (var dir = new DirectoryInfo(start.Value); dir is not null; dir = dir.Parent)
        {
            // A linked worktree has .git as a FILE pointing at the main repo's gitdir; the main
            // checkout has it as a directory. Either one marks a working root.
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return FsPath.From(dir.FullName);
            }
        }

        return start;
    }

    /// <summary>
    /// Where a working root keeps its overlay index. Inside the worktree on purpose: deleting the
    /// worktree takes the overlay with it, so stale overlays cannot accumulate. Requires
    /// <c>.claude/</c> to be git-ignored, or a multi-megabyte binary lands in someone's commit.
    /// </summary>
    public static string OverlayPathFor(FsPath workingRoot) =>
        workingRoot.Combine(".claude", "codesearch", "overlay.cidx").Value;

    /// <summary>For a working root still travelling as text; it is canonicalised on the way in.</summary>
    public static string OverlayPathFor(string workingRoot) =>
        OverlayPathFor(FsPath.From(workingRoot));

    /// <summary>
    /// Resolves the repository root for <paramref name="candidate"/> (or the current directory).
    ///
    /// A git worktree resolves to its MAIN repository, not to itself: this identifies the
    /// repository as a whole, and is what the single shared BASE index is keyed by. Per-branch
    /// differences live in overlays instead of in separate full indexes.
    /// </summary>
    public static FsPath ResolveRoot(string? candidate = null)
    {
        var start = Normalize(candidate);
        var fromGit = TryGitCommonRoot(start);
        if (fromGit is not null)
        {
            return fromGit.Value;
        }

        for (var dir = new DirectoryInfo(start.Value); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return FsPath.From(dir.FullName);
            }
        }

        return start;
    }

    /// <summary>
    /// Index location for a repository. The path hash keeps two checkouts of the same-named repo
    /// (say R:\IntelWash and R:\OldRepo\IntelWash_old) from colliding on one file.
    /// </summary>
    public static string IndexPathFor(string root, string? runtimeRoot = null)
    {
        try
        {
            return RuntimeIndexLayout.ResolveBaseIndexPath(root, runtimeRoot);
        }
        catch (InvalidOperationException)
        {
            return LegacyIndexPathFor(root, runtimeRoot);
        }
    }

    public static string LegacyIndexPathFor(string root, string? runtimeRoot = null)
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

        return Path.Combine(LegacyIndexDirectory(runtimeRoot), $"{name}-{hash}.cidx");
    }

    /// <summary>
    /// Where indexes built before generations existed are kept, inside the named installation.
    /// This was the last path in the layout that reached for the machine's own runtime on its
    /// own, which is enough to put a test's legacy index in the real one.
    /// </summary>
    public static string LegacyIndexDirectory(string? runtimeRoot = null) => Path.Combine(
        string.IsNullOrWhiteSpace(runtimeRoot)
            ? RuntimeIndexLayout.DefaultRuntimeRoot
            : runtimeRoot,
        "repositories",
        "legacy");

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
                CreateNoWindow = true,
                // Standard output here is an archive, copied byte for byte. Only the error
                // stream is read as text, and Git writes UTF-8.
                StandardErrorEncoding = ChildProcessText.Utf8,
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

    private static FsPath Normalize(string? candidate)
    {
        var start = FsPath.From(
            string.IsNullOrWhiteSpace(candidate) ? Environment.CurrentDirectory : candidate);

        // A file names the directory holding it: every caller here is asking "which checkout is
        // this in", and a path to a source file is a perfectly ordinary way to ask that.
        return start.FileExists ? start.Parent ?? start : start;
    }

    private static FsPath? TryGitCommonRoot(FsPath start)
    {
        // --git-common-dir points at the MAIN repo's .git even when run from a linked worktree,
        // which is exactly the redirection we want.
        var commonDir = RunGit(start.Value, "rev-parse --path-format=absolute --git-common-dir");
        if (string.IsNullOrWhiteSpace(commonDir))
        {
            return null;
        }

        // Git prints forward slashes here even on Windows. That used to be repaired by hand,
        // one Replace at a time, in each place that read git output — and the place that forgot
        // is how a live worktree came to hash to a key matching no directory.
        return FsPath.From(commonDir).Parent;
    }

    /// <summary>
    /// A runaway guard, not an expectation.
    ///
    /// <c>git rev-parse</c> answers in milliseconds. Ten seconds was short enough that a loaded
    /// machine -- several MCP clients sharing one server, each spawning git -- could miss it, and
    /// a missed deadline came back as "Git common directory is unavailable": a claim about the
    /// repository, when the truth was about the moment. That is why every retry succeeded.
    ///
    /// Two minutes cannot be mistaken for a performance budget and still bounds a git that has
    /// genuinely hung, which is what this exists for.
    /// </summary>
    private const int GitTimeoutMs = 120_000;

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
    /// <summary>
    /// What running git actually did, so a caller can say which of the several very different
    /// failures it hit. <see cref="RunGit"/> throws all of them away on purpose, because most
    /// callers only want to know whether there is a repository here.
    /// </summary>
    private readonly record struct GitRun(
        string Output,
        int? ExitCode,
        string Error,
        bool TimedOut,
        Exception? Fault)
    {
        public bool Succeeded => ExitCode == 0 && Output.Length > 0;
    }

    private static string? RunGit(string workingDirectory, string arguments)
    {
        var run = Run(workingDirectory, arguments);
        return run.Succeeded ? run.Output : null;
    }

    /// <summary>
    /// Runs git and returns its output, or throws naming what went wrong.
    ///
    /// <see cref="GitOutput"/> cannot tell "there is no repository here" from "git could not be
    /// run just now", so callers turned both into one sentence about the repository. Under
    /// several concurrent MCP clients the second is the one that happens, and the message sent
    /// whoever read it looking in the wrong place.
    /// </summary>
    /// <param name="description">
    /// What was being read, named as the caller would say it -- this becomes the first half of
    /// the message somebody has to act on.
    /// </param>
    public static string GitOutputOrThrow(
        string workingDirectory,
        string arguments,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var run = Run(workingDirectory, arguments);
        if (run.Succeeded)
        {
            return run.Output;
        }

        throw new InvalidOperationException(
            $"{description} could not be read: {Explain(run, arguments)}");
    }

    private static string Explain(GitRun run, string arguments)
    {
        if (run.Fault is { } fault)
        {
            return $"'git {arguments}' could not be started " +
                $"({fault.GetType().Name}: {fault.Message}).";
        }

        if (run.TimedOut)
        {
            return $"'git {arguments}' did not finish within " +
                $"{TimeSpan.FromMilliseconds(GitTimeoutMs).TotalSeconds:0} seconds and was " +
                "stopped. This is a machine under load rather than a broken repository; the " +
                "same call usually succeeds straight afterwards.";
        }

        if (run.ExitCode is { } exitCode && exitCode != 0)
        {
            var detail = FirstLine(run.Error);
            return detail.Length > 0
                ? $"'git {arguments}' exited {exitCode}: {detail}"
                : $"'git {arguments}' exited {exitCode}.";
        }

        return $"'git {arguments}' produced no output.";
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var end = trimmed.IndexOfAny(['\r', '\n']);
        return end < 0 ? trimmed : trimmed[..end];
    }

    private static GitRun Run(string workingDirectory, string arguments)
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
                StandardOutputEncoding = ChildProcessText.Utf8,
                StandardErrorEncoding = ChildProcessText.Utf8,
            });

            if (process is null)
            {
                return new GitRun(string.Empty, null, string.Empty, false, null);
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

                return new GitRun(string.Empty, null, string.Empty, true, null);
            }

            // WaitForExit(int) returns once the process ends but does not await the redirected
            // readers, so the buffers still have to be drained before reading the result.
            if (!Task.WaitAll([stdout, stderr], GitTimeoutMs))
            {
                return new GitRun(string.Empty, null, string.Empty, true, null);
            }

            return new GitRun(
                stdout.Result.Trim(),
                process.ExitCode,
                stderr.Result,
                false,
                null);
        }
        catch (Exception exception)
        {
            return new GitRun(string.Empty, null, string.Empty, false, exception);
        }
    }
}
