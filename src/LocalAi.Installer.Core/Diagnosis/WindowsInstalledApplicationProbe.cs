using System.Diagnostics;
using System.Runtime.Versioning;
using LocalAi.Installer.Core.Abstractions;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class WindowsInstalledApplicationProbe : IInstalledApplicationProbe
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<InstalledApplicationMetadata?> FindOllamaAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<InstalledApplicationMetadata?>(null);
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = FindInRegistry(hive, view, cancellationToken);
                if (match is not null)
                {
                    return Task.FromResult<InstalledApplicationMetadata?>(match);
                }
            }
        }

        return Task.FromResult<InstalledApplicationMetadata?>(null);
    }

    [SupportedOSPlatform("windows")]
    private static InstalledApplicationMetadata? FindInRegistry(
        RegistryHive hive,
        RegistryView view,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                return null;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var application = uninstall.OpenSubKey(subKeyName);
                var displayName = application?.GetValue("DisplayName") as string;
                if (displayName is null ||
                    !displayName.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var installLocation = application!.GetValue("InstallLocation") as string;
                var executablePath = ResolveExecutablePath(
                    application.GetValue("DisplayIcon") as string,
                    installLocation);
                return new InstalledApplicationMetadata(
                    displayName,
                    application.GetValue("DisplayVersion") as string,
                    installLocation,
                    executablePath,
                    ReadFileVersion(executablePath));
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            System.Security.SecurityException or IOException)
        {
        }

        return null;
    }

    private static string? ReadFileVersion(string? executablePath)
    {
        if (executablePath is null)
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? ResolveExecutablePath(
        string? displayIcon,
        string? installLocation)
    {
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var iconPath = displayIcon.Trim().Trim('"');
            var comma = iconPath.LastIndexOf(',');
            if (comma > 0 && int.TryParse(iconPath[(comma + 1)..], out _))
            {
                iconPath = iconPath[..comma].Trim().Trim('"');
            }

            if (File.Exists(iconPath))
            {
                return Path.GetFullPath(iconPath);
            }
        }

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            var candidate = Path.Combine(installLocation, "ollama.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
