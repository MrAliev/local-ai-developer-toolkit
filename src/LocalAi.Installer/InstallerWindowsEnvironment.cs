using System.IO;

namespace LocalAi.Installer;

internal static class InstallerWindowsEnvironment
{
    public static void EnsureValidWindowsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("windir");
        var resolved = ResolveWindowsDirectory(configured, Environment.SystemDirectory);
        if (!string.Equals(configured, resolved, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable(
                "windir",
                resolved,
                EnvironmentVariableTarget.Process);
        }
    }

    internal static string ResolveWindowsDirectory(string? configured, string systemDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        }

        if (string.IsNullOrWhiteSpace(systemDirectory) ||
            !Path.IsPathFullyQualified(systemDirectory))
        {
            throw new InvalidOperationException("The Windows directory could not be determined.");
        }

        var parent = Directory.GetParent(Path.GetFullPath(systemDirectory));
        if (parent is null)
        {
            throw new InvalidOperationException("The Windows directory could not be determined.");
        }

        return Path.TrimEndingDirectorySeparator(parent.FullName);
    }
}
