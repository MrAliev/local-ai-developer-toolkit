using System.Diagnostics;
using System.Runtime.Versioning;
using LocalAi.Installer.Core.Activation;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Removal;

/// <summary>What Apps &amp; features shows for this installation.</summary>
public sealed record UninstallEntry(
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    string UninstallString,
    string InstallLocation,
    int EstimatedSizeKilobytes);

/// <summary>
/// The entry in Apps &amp; features, and the copy of the installer it points at.
///
/// Removal is only a first-class way out if it can be found where people look for it, which
/// on Windows is that list. The registration is per-user (HKCU) because the installation is:
/// everything lives under %LOCALAPPDATA% and no part of it needs administrator rights, and an
/// HKLM entry would offer every account on the machine an uninstall for something only one of
/// them has.
///
/// The uninstaller is a copy of this installer parked inside the runtime root. It has to be a
/// copy: the file somebody downloaded is routinely gone by the time they want to remove
/// anything, and an UninstallString pointing at a missing file is worse than no entry at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UninstallRegistration(
    InstallationLayout layout,
    string? registrySubKey = null,
    Action<string>? removeAfterExit = null)
{
    /// <summary>
    /// How a directory this process is running from gets deleted once it exits. Replaceable so
    /// a test can observe that the deferral happened without spawning anything.
    /// </summary>
    private readonly Action<string> removeAfterExit = removeAfterExit ?? RemoveAfterExit;

    /// <summary>
    /// Where Windows looks. The leaf name is the product key, and it is stable across
    /// versions: an upgrade updates this entry rather than adding a second one.
    /// </summary>
    public const string DefaultSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAi";

    public const string DisplayName = "LocalAi Developer Toolkit";

    public const string Publisher = "MrAliev";

    /// <summary>The directory holding the uninstaller's copy, inside the runtime root.</summary>
    public const string UninstallerDirectoryName = "uninstall";

    public const string UninstallerFileName = "LocalAi.Installer.exe";

    private readonly string subKey = registrySubKey ?? DefaultSubKey;

    public string UninstallerDirectory =>
        Path.Combine(layout.BinRoot, UninstallerDirectoryName);

    public string UninstallerPath =>
        Path.Combine(UninstallerDirectory, UninstallerFileName);

    /// <summary>
    /// Copies this installer into the runtime root and writes the entry pointing at the copy.
    ///
    /// An upgrade runs this again: the same key is rewritten, so DisplayVersion follows the
    /// installation instead of naming whichever release first created the entry.
    /// </summary>
    public UninstallEntry Register(string version, string installerSourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerSourcePath);
        CopyUninstaller(installerSourcePath);
        var entry = new UninstallEntry(
            DisplayName,
            version,
            Publisher,
            "\"" + UninstallerPath + "\" " + UninstallSwitch,
            layout.Root,
            EstimatedSizeKilobytes());
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue("DisplayName", entry.DisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", entry.DisplayVersion, RegistryValueKind.String);
        key.SetValue("Publisher", entry.Publisher, RegistryValueKind.String);
        key.SetValue("UninstallString", entry.UninstallString, RegistryValueKind.String);
        key.SetValue("InstallLocation", entry.InstallLocation, RegistryValueKind.String);
        key.SetValue("EstimatedSize", entry.EstimatedSizeKilobytes, RegistryValueKind.DWord);
        // There is no repair or modify path: the wizard's own start page is where those live,
        // and offering buttons here that lead nowhere is worse than not offering them.
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        return entry;
    }

    public UninstallEntry? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        if (key is null)
        {
            return null;
        }

        return new UninstallEntry(
            key.GetValue("DisplayName") as string ?? string.Empty,
            key.GetValue("DisplayVersion") as string ?? string.Empty,
            key.GetValue("Publisher") as string ?? string.Empty,
            key.GetValue("UninstallString") as string ?? string.Empty,
            key.GetValue("InstallLocation") as string ?? string.Empty,
            key.GetValue("EstimatedSize") as int? ?? 0);
    }

    /// <summary>
    /// Takes the entry out of Apps &amp; features. Absent is success: an uninstall run on a
    /// machine whose entry was already removed by hand has nothing left to do here.
    /// </summary>
    public bool Unregister()
    {
        try
        {
            var parent = subKey[..subKey.LastIndexOf('\\')];
            var leaf = subKey[(subKey.LastIndexOf('\\') + 1)..];
            using var key = Registry.CurrentUser.OpenSubKey(parent, writable: true);
            if (key?.OpenSubKey(leaf) is null)
            {
                return false;
            }

            key.DeleteSubKeyTree(leaf);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the uninstaller's own copy, last of all.
    ///
    /// Windows will not delete a running executable, and this run is that executable whenever
    /// it was started from Apps &amp; features. The ordinary delete is tried first — it
    /// succeeds when the person is running an installer from somewhere else — and otherwise
    /// the directory is handed to a small process that outlives this one.
    ///
    /// Not <c>MoveFileEx(…, MOVEFILE_DELAY_UNTIL_REBOOT)</c>, which is the textbook answer:
    /// it works by writing PendingFileRenameOperations under HKLM and therefore needs
    /// administrator rights. This installation is per-user by design and has none, so that
    /// call would fail silently and leave the directory behind forever.
    ///
    /// Returns whether the directory is gone now; false means "in a moment, once this process
    /// exits", which the finish page says out loud rather than leaving a folder to be found
    /// later and wondered about.
    /// </summary>
    public bool RemoveUninstallerCopy()
    {
        if (!Directory.Exists(UninstallerDirectory))
        {
            return true;
        }

        try
        {
            Directory.Delete(UninstallerDirectory, recursive: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            removeAfterExit(UninstallerDirectory);
            return false;
        }
    }

    /// <summary>
    /// Deletes the directory once nothing is holding it, from a process that outlives this
    /// one.
    ///
    /// It waits on the condition rather than on a duration: each attempt removes the directory
    /// and stops the moment it is gone, so it finishes as soon as this process exits instead
    /// of after a guessed-at delay. The attempts are bounded because a tail process that never
    /// ends is worse than a directory that stays.
    /// </summary>
    private static void RemoveAfterExit(string directory)
    {
        var quoted = "\"" + directory + "\"";
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(
            "for /l %i in (1,1,60) do (" +
            "rd /s /q " + quoted + " 2>nul & " +
            "if not exist " + quoted + " exit /b 0 & " +
            "timeout /t 1 /nobreak >nul)");
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing further to try: the entry is already gone, so the directory is inert
            // even if it stays. The caller reports that it was left behind.
        }
    }

    /// <summary>The argument the entry passes back to the copied installer.</summary>
    public const string UninstallSwitch = "--uninstall";

    private void CopyUninstaller(string installerSourcePath)
    {
        Directory.CreateDirectory(UninstallerDirectory);
        var source = Path.GetFullPath(installerSourcePath);
        if (string.Equals(
                source,
                Path.GetFullPath(UninstallerPath),
                StringComparison.OrdinalIgnoreCase))
        {
            // Already the copy — an uninstaller that was asked to install, or a repair run
            // started from the parked executable. Copying a file onto itself throws.
            return;
        }

        File.Copy(source, UninstallerPath, overwrite: true);
    }

    /// <summary>
    /// What Apps &amp; features prints beside the entry. Reported in kilobytes because that is
    /// the unit the registry value is defined in, and measured rather than guessed: this
    /// installation's size is mostly its indexes, which vary by an order of magnitude between
    /// machines.
    /// </summary>
    private int EstimatedSizeKilobytes()
    {
        if (!Directory.Exists(layout.Root))
        {
            return 0;
        }

        try
        {
            var bytes = Directory
                .EnumerateFiles(layout.Root, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length;
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        return 0L;
                    }
                });
            return (int)Math.Clamp(bytes / 1024, 0, int.MaxValue);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

}
