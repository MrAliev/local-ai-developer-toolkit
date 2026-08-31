namespace LocalAi.Repository;

/// <summary>
/// Where the managed Git hooks live, and what marks them as ours.
///
/// Installation and removal have to agree about this exactly, and they run from different
/// executables: the CLI installs the dispatchers, the installer wizard takes them out again.
/// A second copy of the husky rule or of the dispatcher marker would not fail loudly — it
/// would leave one side unable to find what the other side wrote, which is the same silence
/// that made hooks stop running when only <c>$GIT_DIR/hooks</c> was considered.
/// </summary>
public static class GitHookLayout
{
    /// <summary>The dispatcher's first comment line, and the only proof a hook file is ours.</summary>
    public const string DispatcherMarker = "# LocalAi managed dispatcher";

    /// <summary>Where a hook that was already there is parked so the dispatcher can chain it.</summary>
    public const string ChainedSuffix = ".pre-localai";

    public const string ExcludeHeader = "# LocalAi managed Git hooks";

    public static IReadOnlyList<string> Events { get; } = Array.AsReadOnly(
    [
        "post-commit",
        "post-merge",
        "post-rewrite",
        "post-checkout"
    ]);

    /// <summary>
    /// Where Git will actually look for hooks in this repository.
    /// </summary>
    /// <remarks>
    /// Writing to <c>$GIT_DIR/hooks</c> unconditionally is wrong for any repository that sets
    /// <c>core.hooksPath</c>, which is most front-end ones: husky, lefthook and simple-git-hooks
    /// all set it. Git then never looks at <c>$GIT_DIR/hooks</c>, so the dispatchers install
    /// successfully and never run again, and the index falls behind HEAD with nothing to say so.
    /// </remarks>
    public static string ResolveHooksDirectory(
        string commonDirectory,
        string? configuredHooksPath,
        string? workingTreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonDirectory);
        var common = Path.GetFullPath(commonDirectory);
        if (string.IsNullOrWhiteSpace(configuredHooksPath))
        {
            return Path.Combine(common, "hooks");
        }

        // Git resolves a relative core.hooksPath against the directory a hook runs in, which is
        // the top of the working tree. A bare repository has none, and then the common directory
        // is the only thing left to resolve against.
        var configured = configuredHooksPath.Trim();
        var resolved = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(
                string.IsNullOrWhiteSpace(workingTreeRoot) ? common : workingTreeRoot,
                configured));

        // husky points core.hooksPath at `.husky/_`, a directory it rewrites from scratch on
        // every `husky` run — so an npm install would silently delete anything left there. Its
        // shims run `.husky/<hook>` instead, which husky never overwrites, and which every
        // installed shim already delegates to whether or not that file exists yet.
        return IsHuskyRunnerDirectory(resolved)
            ? Path.GetDirectoryName(resolved.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))!
            : resolved;
    }

    /// <summary>
    /// Whether a hook file was written by LocalAi. A hook the user wrote themselves carries no
    /// marker and is never touched — not on install, where it is chained, and not on removal,
    /// where it is left exactly as found.
    /// </summary>
    public static bool IsManagedDispatcher(string hookPath)
    {
        try
        {
            return File.Exists(hookPath) &&
                File.ReadAllText(hookPath).Contains(DispatcherMarker, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <c>.git/info/exclude</c> patterns installation writes for a hooks directory that
    /// sits inside the working tree, in the order it writes them.
    /// </summary>
    public static IReadOnlyList<string> ExcludePatterns(
        string workingTreeRoot,
        IEnumerable<string> hookPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingTreeRoot);
        ArgumentNullException.ThrowIfNull(hookPaths);
        var root = Path.GetFullPath(workingTreeRoot);
        return hookPaths
            .Select(path => "/" + Path.GetRelativePath(root, path).Replace('\\', '/'))
            .SelectMany(pattern => new[] { pattern, pattern + ChainedSuffix })
            .ToArray();
    }

    /// <summary>
    /// Whether the hooks directory is part of the working tree, and therefore turns up in
    /// <c>git status</c>. <c>$GIT_DIR/hooks</c> sits under the working tree by path but is no
    /// part of it.
    /// </summary>
    public static bool IsInsideWorkingTree(
        string hooksDirectory,
        string commonDirectory,
        string? workingTreeRoot) =>
        IsInside(hooksDirectory, workingTreeRoot) &&
        !IsInside(hooksDirectory, commonDirectory);

    private static bool IsInside(string path, string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return false;
        }

        var root = Path.GetFullPath(container)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(path).StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHuskyRunnerDirectory(string directory)
    {
        var trimmed = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(trimmed), "_", StringComparison.Ordinal))
        {
            return false;
        }

        return Path.GetDirectoryName(trimmed) is { Length: > 0 } &&
               (File.Exists(Path.Combine(trimmed, "h")) ||
                File.Exists(Path.Combine(trimmed, "husky.sh")));
    }
}
