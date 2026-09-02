using LocalAi.Installer.ViewModels;
using System.Globalization;
using System.Windows;
using LocalAi.Installer.Core;

namespace LocalAi.Installer;

public partial class App : Application
{
    /// <summary>
    /// What Apps &amp; features passes back to this executable to mean "remove it". The same
    /// binary installs and uninstalls: everything removal needs — the client adapters, the
    /// managed-block machinery, the journal, the review page — already lives here, and it runs
    /// from outside the tree it deletes.
    /// </summary>
    public const string UninstallSwitch = "--uninstall";

    private InstallerThemeSwitch? themes;

    public App() =>
        InstallerWindowsEnvironment.EnsureValidWindowsDirectory();

    /// <summary>
    /// The live palette, for the windows that have chrome the resources cannot reach — the
    /// caption is drawn by the desktop manager and has to be told separately.
    /// </summary>
    public static InstallerThemeSwitch? Themes => (Current as App)?.themes;

    /// <summary>
    /// Whether this command line asks for removal. Case-insensitive, and accepts the
    /// slash form Windows callers still write, because an uninstall command a shell quotes
    /// slightly differently must not silently start an installer instead.
    /// </summary>
    public static bool IsUninstallRequested(IEnumerable<string>? arguments) =>
        arguments?.Any(argument =>
            string.Equals(argument, UninstallSwitch, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "-uninstall", StringComparison.OrdinalIgnoreCase)) ?? false;

    /// <summary>
    /// Apps &amp; features asks for removal directly, so that argument goes straight to the
    /// uninstall wizard. Started with no argument — from Explorer, or from a download — the
    /// same executable asks what the person came to do, because it is equally the installer,
    /// the updater, the repair tool and the uninstaller.
    /// </summary>
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Before any window is built, and on both paths. The uninstall path arrives from Apps
        // and features, never sees the start screen, and would otherwise inherit nothing —
        // somebody who chose Russian to install would be removed from in English.
        var preferences = InstallerPreferencesStore.Default;
        InstallerCulture.Current = preferences.ReadLanguage(CultureInfo.CurrentUICulture);

        // Before any window is built, for the same reason as the language: a window that
        // opened light and repainted a frame later is a flash of the wrong theme on the one
        // screen somebody is looking at while they wait.
        themes = new InstallerThemeSwitch(Resources, preferences.ReadTheme());
        themes.Apply();

        Window window = IsUninstallRequested(e.Args)
            ? new UninstallWindow()
            : new StartWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// SystemEvents is a static root: left subscribed, it keeps the switch — and through it the
    /// window it repaints — alive for the life of the process.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        themes?.Dispose();
        themes = null;
        base.OnExit(e);
    }
}
