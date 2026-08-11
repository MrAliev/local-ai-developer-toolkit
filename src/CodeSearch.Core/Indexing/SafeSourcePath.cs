namespace CodeSearch.Core.Indexing;

internal enum SourcePathFailure
{
    None,
    OutsideRoot,
    ReparsePoint,
    Missing,
    Unavailable
}

/// <summary>
/// Resolves an indexed relative path without allowing it to escape the repository lexically or
/// through a symbolic link/reparse point.
/// </summary>
internal static class SafeSourcePath
{
    /// <summary>
    /// How the walk learns what each path component is. Injectable for one reason: creating a
    /// real symbolic link on Windows needs Developer Mode or administrator rights, so the tests
    /// covering this guard skipped on every machine that had neither — which is most of them,
    /// including any CI runner. A guard against escaping the repository is a poor thing to leave
    /// exercised only where someone happened to have the privilege.
    ///
    /// The real-link tests stay: they prove the attribute actually appears on a real link, which
    /// a fake cannot. This makes the walk itself testable everywhere.
    /// </summary>
    public static bool TryResolveFile(
        string root,
        string relativePath,
        out string fullPath,
        out SourcePathFailure failure,
        Func<string, FileAttributes>? getAttributes = null)
    {
        if (!TryResolveExisting(root, relativePath, out fullPath, out failure, getAttributes))
        {
            return false;
        }

        if (File.Exists(fullPath))
        {
            return true;
        }

        failure = SourcePathFailure.Missing;
        return false;
    }

    public static bool TryResolveExisting(
        string root,
        string relativePath,
        out string fullPath,
        out SourcePathFailure failure,
        Func<string, FileAttributes>? getAttributes = null)
    {
        getAttributes ??= File.GetAttributes;
        fullPath = string.Empty;
        failure = SourcePathFailure.None;

        string fullRoot;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = SourcePathFailure.OutsideRoot;
            return false;
        }

        var confinedPath = Path.GetRelativePath(fullRoot, fullPath);
        if (IsOutside(confinedPath))
        {
            failure = SourcePathFailure.OutsideRoot;
            return false;
        }

        var currentPath = fullRoot;
        foreach (var component in confinedPath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            FileAttributes attributes;
            try
            {
                attributes = getAttributes(currentPath);
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or DirectoryNotFoundException)
            {
                failure = SourcePathFailure.Missing;
                return false;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                failure = SourcePathFailure.Unavailable;
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                failure = SourcePathFailure.ReparsePoint;
                return false;
            }
        }

        return true;
    }

    private static bool IsOutside(string relativePath) =>
        Path.IsPathRooted(relativePath) ||
        relativePath.Equals("..", StringComparison.Ordinal) ||
        relativePath.StartsWith(
            ".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) ||
        relativePath.StartsWith(
            ".." + Path.AltDirectorySeparatorChar,
            StringComparison.Ordinal);
}
