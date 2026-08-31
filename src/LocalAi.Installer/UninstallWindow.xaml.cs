using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer;

/// <summary>
/// The wizard in uninstall mode. Navigation lives in the view model, so this file only
/// forwards the events WPF cannot express as a binding.
/// </summary>
public partial class UninstallWindow : Window
{
    private readonly UninstallWizardViewModel viewModel;

    public UninstallWindow(
        RemovalPreset preset = RemovalPreset.FullUninstall,
        bool offersInstallAfterwards = false)
    {
        viewModel = new UninstallWizardViewModel(preset, offersInstallAfterwards);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.InstallRequested += (_, _) =>
        {
            var installer = new MainWindow();
            Application.Current.MainWindow = installer;
            installer.Show();
            Close();
        };
    }

    // async void, so an escaping exception would land on the dispatcher and kill the wizard;
    // it goes into the view model's error state instead, exactly as the install window does.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            viewModel.ReportUnexpectedError(exception);
        }
    }

    private void OnPresetChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: RemovalPreset preset })
        {
            viewModel.SelectedPreset = preset;
        }
    }
}
