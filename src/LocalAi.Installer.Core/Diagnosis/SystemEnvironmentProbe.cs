using System.Runtime.InteropServices;
using LocalAi.Installer.Core.Abstractions;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class SystemEnvironmentProbe : IEnvironmentProbe
{
    public HostEnvironmentSnapshot GetHost() =>
        new(
            OperatingSystem.IsWindows(),
            ReadWindowsProductName(),
            Environment.OSVersion.Version,
            RuntimeInformation.OSArchitecture);

    public string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? ResolveExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        if (Path.IsPathFullyQualified(executableName))
        {
            return File.Exists(executableName)
                ? Path.GetFullPath(executableName)
                : null;
        }

        foreach (var directory in SearchDirectories())
        {
            foreach (var name in CandidateNames(executableName))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every directory worth searching, in order, without repeats.
    /// </summary>
    private static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
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

    /// <summary>
    /// The machine and user PATH as the registry holds them now, then the inherited process PATH.
    ///
    /// Windows hands PATH to a process by inheriting the parent's environment block at creation,
    /// not by reading the registry. A dependency installed during an open session is therefore
    /// invisible to everything descended from a shell that started earlier - and Explorer starts
    /// at logon, so that is nearly everything. Installing the GitHub CLI and immediately running
    /// this installer produced exactly that: `gh.exe` sat in the machine PATH, `where gh` found
    /// it from a fresh shell, and the installer reported "Not found" and refused to continue,
    /// because it had been launched from a browser descended from the pre-install Explorer.
    ///
    /// Reading the registry values closes that gap without asking anyone to sign out first.
    /// </summary>
    private static IEnumerable<string> PathSources()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            yield break;
        }

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

    /// <summary>
    /// On Windows, PATHEXT candidates must come before the extensionless name. npm installs a
    /// POSIX shell script and a Windows .cmd shim side by side; choosing the extensionless file
    /// makes Process.Start fail even though the dependency is correctly installed.
    /// </summary>
    private static IEnumerable<string> CandidateNames(string executableName)
        => CandidateNames(
            executableName,
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATHEXT"));

    internal static IEnumerable<string> CandidateNames(
        string executableName,
        bool isWindows,
        string? pathExt)
    {
        if (!isWindows || Path.HasExtension(executableName))
        {
            yield return executableName;
            yield break;
        }

        var extensions = string.IsNullOrWhiteSpace(pathExt)
            ? [".COM", ".EXE", ".BAT", ".CMD"]
            : pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var extension in extensions)
        {
            var trimmed = extension.Trim();
            if (trimmed.StartsWith('.'))
            {
                yield return executableName + trimmed;
            }
        }

        yield return executableName;
    }

    private static string ReadWindowsProductName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return RuntimeInformation.OSDescription;
        }

        try
        {
            return Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "ProductName",
                    null) as string
                ?? RuntimeInformation.OSDescription;
        }
        catch (System.Security.SecurityException)
        {
            return RuntimeInformation.OSDescription;
        }
    }
}
