using System.Windows;

namespace LocalAi.Installer;

public partial class App : Application
{
    public App()
    {
        InstallerWindowsEnvironment.EnsureValidWindowsDirectory();
    }
}
