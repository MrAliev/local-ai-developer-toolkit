using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer;

/// <summary>
/// The front door. One executable installs, updates, repairs, reinstalls and removes, so the
/// first question it asks is which of those this is — answered against what is actually on
/// the machine rather than by offering all five and failing on four of them.
/// </summary>
public partial class StartWindow : Window
{
    private readonly InstallerStartViewModel viewModel = new();

    public StartWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnChooseLanguage(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } &&
            Enum.TryParse<InstallerLanguage>(name, out var language))
        {
            viewModel.ChooseLanguage(language);
        }
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StartChoice choice })
        {
            return;
        }

        Open(choice switch
        {
            // The errand travels with the window: an update that asks every question an
            // install asks is a wizard that did not hear the answer already given (#257).
            StartChoice.Install or StartChoice.UpdateOrRepair => new MainWindow(choice),
            // The removal half opens on the reinstall-friendly row and offers the install half
            // when it finishes; two deliberate wizards, because the install has prerequisites
            // and a release choice of its own to confirm.
            StartChoice.CleanReinstall => new UninstallWindow(
                InstallerStartViewModel.PresetFor(choice),
                offersInstallAfterwards: true),
            _ => new UninstallWindow(InstallerStartViewModel.PresetFor(choice)),
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Open(Window window)
    {
        Application.Current.MainWindow = window;
        window.Show();
        Close();
    }
}
