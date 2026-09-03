using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Which of the two palettes to paint, and where the system's answer comes from.
///
/// The decision is separated from the registry read so it can be stated as a table and tested
/// as one. What the registry holds on a given machine is not something a test can arrange, and
/// a test that read it would assert whatever that machine happens to be set to — passing either
/// way, which is no test at all.
/// </summary>
public static class InstallerThemes
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AppsUseLightTheme = "AppsUseLightTheme";

    /// <summary>
    /// An explicit choice outranks the system; <see cref="InstallerTheme.System"/> follows it.
    /// </summary>
    public static bool IsDark(InstallerTheme chosen, bool systemPrefersDark) =>
        chosen switch
        {
            InstallerTheme.Light => false,
            InstallerTheme.Dark => true,
            _ => systemPrefersDark,
        };

    /// <summary>
    /// Windows writes `AppsUseLightTheme` only once somebody has changed the setting, so a
    /// machine nobody has touched has no value at all rather than a light one. Absent, and
    /// anything that is not zero, reads as light.
    /// </summary>
    public static bool PrefersDark(int? appsUseLightTheme) => appsUseLightTheme == 0;

    /// <summary>
    /// What Windows says right now. Read on every ask rather than cached: the setting can
    /// change while the installer is open, and that is exactly what "System" promises to follow.
    ///
    /// Any failure reads as light. A preference nobody can read is not a reason to fail a run,
    /// and light is what the installer looked like before it could read one at all.
    /// </summary>
    public static bool SystemPrefersDark()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return PrefersDark(ReadAppsUseLightTheme());
    }

    [SupportedOSPlatform("windows")]
    private static int? ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightTheme) as int?;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or UnauthorizedAccessException
                or IOException)
        {
            return null;
        }
    }
}
