using LocalAi.Installer.ViewModels;
using System.Globalization;
using System.Windows;

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

    public App() =>
        InstallerWindowsEnvironment.EnsureValidWindowsDirectory();

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
        InstallerCulture.Current = InstallerLanguageStore.Default.Read(CultureInfo.CurrentUICulture);

        Window window = IsUninstallRequested(e.Args)
            ? new UninstallWindow()
            : new StartWindow();
        MainWindow = window;
        window.Show();
    }
}
