using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer;

/// <summary>
/// Navigation lives in the view model and is bound through commands, so this file only
/// forwards the few control events that WPF cannot express as a two-way binding.
/// </summary>
public partial class MainWindow : Window
{
    private readonly InstallerWizardViewModel viewModel = new() { EnableDependencyActions = true };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    // Both async void handlers route unexpected exceptions into the view model's error
    // state: escaping ones would land on the dispatcher and kill the installer (#209/m4).
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

    private void OnDependencyConsentChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string dependencyId })
        {
            return;
        }

        viewModel.Dependencies.SetConsent(dependencyId, ((CheckBox)sender).IsChecked == true);
        viewModel.RefreshNavigationState();
    }

    private async void OnPackageCheckRelease(object sender, RoutedEventArgs e)
    {
        try
        {
            await viewModel.ResolvePackageAsync();
        }
        catch (Exception exception)
        {
            viewModel.ReportUnexpectedError(exception);
        }
    }

    private void OnAgentChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string agent } comboBox ||
            comboBox.SelectedItem is not AgentChoice choice)
        {
            return;
        }

        viewModel.Agents.SetChoice(agent, choice);
        viewModel.RefreshNavigationState();
    }

    private void OnReviewConfirmedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            viewModel.SetReviewConfirmed(checkBox.IsChecked == true);
        }
    }
}
