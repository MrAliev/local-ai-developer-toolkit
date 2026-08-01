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

        foreach (var pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = pathEntry.Trim().Trim('"');
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
    /// The bare name first, then each PATHEXT extension. Only the bare name used to be tried,
    /// so resolving "gh" searched for a file literally called `gh` and found nothing next to
    /// `gh.exe`. Every current caller happens to pass a full name like `winget.exe`, which is
    /// why that never showed - the next caller to pass a bare command would have hit it.
    /// </summary>
    private static IEnumerable<string> CandidateNames(string executableName)
    {
        yield return executableName;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(executableName))
        {
            yield break;
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
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
