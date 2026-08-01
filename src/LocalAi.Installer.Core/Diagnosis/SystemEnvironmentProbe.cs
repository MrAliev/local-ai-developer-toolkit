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
            var candidate = Path.Combine(pathEntry.Trim().Trim('"'), executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
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
