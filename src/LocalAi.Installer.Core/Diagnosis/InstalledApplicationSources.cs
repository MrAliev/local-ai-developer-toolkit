using System.Diagnostics;
using System.Runtime.Versioning;
using LocalAi.Installer.Core.Abstractions;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed record UninstallEntrySnapshot(
    string RegistryKeyName,
    string? DisplayName,
    string? DisplayVersion,
    string? InstallLocation,
    string? DisplayIcon,
    string? UninstallString);

public sealed record ExecutableIdentitySnapshot(
    string Path,
    bool Exists,
    string? FileVersion,
    string? ProductName,
    string? OriginalFileName)
{
    public static ExecutableIdentitySnapshot Absent(string path) =>
        new(path, false, null, null, null);
}

public interface IUninstallEntrySource
{
    IReadOnlyList<UninstallEntrySnapshot> ReadEntries(
        CancellationToken cancellationToken);
}

public interface IExecutableIdentityProbe
{
    ExecutableIdentitySnapshot Inspect(string path);
}

public interface IPhysicalPathResolver
{
    string ResolvePhysicalPath(string path);
}

public sealed class SystemPhysicalPathResolver : IPhysicalPathResolver
{
    private readonly SystemFileSystemProbe _fileSystem = new();

    public string ResolvePhysicalPath(string path) =>
        _fileSystem.ResolvePhysicalPath(path);
}

public sealed class SystemExecutableIdentityProbe : IExecutableIdentityProbe
{
    public ExecutableIdentitySnapshot Inspect(string path)
    {
        if (!File.Exists(path))
        {
            return ExecutableIdentitySnapshot.Absent(path);
        }

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return new ExecutableIdentitySnapshot(
                Path.GetFullPath(path),
                true,
                version.FileVersion,
                version.ProductName,
                version.OriginalFilename);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            return ExecutableIdentitySnapshot.Absent(path);
        }
    }
}

public sealed class WindowsRegistryUninstallEntrySource : IUninstallEntrySource
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<UninstallEntrySnapshot> ReadEntries(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var entries = new List<UninstallEntrySnapshot>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadEntries(hive, view, entries, cancellationToken);
            }
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    [SupportedOSPlatform("windows")]
    private static void ReadEntries(
        RegistryHive hive,
        RegistryView view,
        ICollection<UninstallEntrySnapshot> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                return;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var application = uninstall.OpenSubKey(subKeyName);
                if (application is null)
                {
                    continue;
                }

                entries.Add(new UninstallEntrySnapshot(
                    subKeyName,
                    application.GetValue("DisplayName") as string,
                    application.GetValue("DisplayVersion") as string,
                    application.GetValue("InstallLocation") as string,
                    application.GetValue("DisplayIcon") as string,
                    application.GetValue("UninstallString") as string));
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            System.Security.SecurityException or IOException)
        {
        }
    }
}
