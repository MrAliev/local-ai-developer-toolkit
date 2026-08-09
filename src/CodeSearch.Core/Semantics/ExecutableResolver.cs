namespace CodeSearch.Core.Semantics;

/// <summary>
/// Finds the file a command name actually runs.
///
/// This existed twice, and the two copies disagreed on the case that matters. npm installs two
/// files for a command on Windows: a POSIX shell script with no extension and a <c>.cmd</c> shim.
/// <c>File.Exists</c> is true for the extensionless one, but it is not a valid Win32 executable,
/// so a resolver that checks it first hands back a file that cannot be started.
///
/// The SCIP adapters were fixed for that in 0.1.24–0.1.27; the language-server client kept its
/// own copy and its own bug, checking the bare file before the shims. It went unnoticed because
/// live navigation is opt-in and its tests skip without the servers installed — the same shape as
/// the scip-python failure that made every Python repository unable to publish an index.
///
/// One implementation now, and it is the one that matches PATHEXT semantics.
/// </summary>
public static class ExecutableResolver
{
    /// <summary>
    /// Resolves <paramref name="executable"/> to a full path, or returns it unchanged when it
    /// cannot be found — the caller then gets the operating system's own error, which names the
    /// command rather than a path this method invented.
    /// </summary>
    public static string Resolve(string executable) => Find(executable) ?? executable;

    /// <summary>
    /// The resolved path when the command can actually be run, or null. Use this where "not
    /// installed" is a decision rather than an error — a test that should skip, a probe that
    /// should report.
    ///
    /// Null is not the same as "no such file": on Windows an extensionless npm script exists and
    /// still cannot be started, so it is reported as absent rather than handed back.
    /// </summary>
    public static string? Find(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (Path.IsPathRooted(executable) ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return ResolveCandidate(executable);
        }

        foreach (var directory in SearchDirectories())
        {
            if (ResolveCandidate(Path.Combine(directory, executable)) is { } candidate)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The directories a command is looked for in, in priority order. Also handed to child
    /// processes as their PATH: an installer can add Node, Python or npm shims after the parent
    /// started, and the parent's inherited copy would not have them.
    /// </summary>
    public static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            // npm's global prefix is not always on the PATH of the process that inherited its
            // environment before npm was installed.
            var npmDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm");
            if (seen.Add(npmDirectory))
            {
                yield return npmDirectory;
            }
        }

        foreach (var source in PathSources())
        {
            foreach (var entry in source.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = entry.Trim().Trim('"');
                if (directory.Length > 0 && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }
    }

    private static IEnumerable<string> PathSources()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            yield break;
        }

        // A process started before an installer edited the machine or user PATH keeps the old
        // copy, so the registry values are consulted as well as the inherited one.
        foreach (var target in new[]
                 {
                     EnvironmentVariableTarget.Machine,
                     EnvironmentVariableTarget.User,
                 })
        {
            string? value;
            try
            {
                value = Environment.GetEnvironmentVariable("Path", target);
            }
            catch (System.Security.SecurityException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        yield return Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    }

    private static string? ResolveCandidate(string candidate)
    {
        if (OperatingSystem.IsWindows() && !Path.HasExtension(candidate))
        {
            // The extensionless npm script exists on Windows and cannot be executed there.
            // PATHEXT order decides, and the bare file is not a fallback: handing it back
            // produces "not a valid Win32 application" from somewhere far from here.
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                if (File.Exists(candidate + extension))
                {
                    return Path.GetFullPath(candidate + extension);
                }
            }

            return null;
        }

        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }
}
