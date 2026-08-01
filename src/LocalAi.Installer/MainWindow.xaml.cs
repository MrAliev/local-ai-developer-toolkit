using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerWizardViewModel viewModel = new() { EnableDependencyActions = true };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
    }

    private void OnPreviousClicked(object sender, RoutedEventArgs e)
    {
        viewModel.MovePrevious();
    }

    private async void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (viewModel.CurrentPage == InstallerPage.ReviewApply)
        {
            await viewModel.RunAsync();
            return;
        }

        viewModel.MoveNext();
    }

    private async void OnRunClicked(object sender, RoutedEventArgs e)
    {
        await viewModel.RunAsync();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnDependencyConsentChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox)
        {
            return;
        }

        var dependencyId = checkBox.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(dependencyId))
        {
            return;
        }

        viewModel.Dependencies.SetConsent(dependencyId, checkBox.IsChecked == true);
        viewModel.RefreshNavigationState();
    }

    private void OnPackageSelectRelease(object sender, RoutedEventArgs e)
    {
        if (PackageVersionBox.Text is not string version || string.IsNullOrWhiteSpace(version))
        {
            version = "latest";
        }

        viewModel.Package.SelectCompatibleRelease(version.Trim(), true);
        viewModel.RefreshNavigationState();
    }

    private void OnModelModeAutomatic(object sender, RoutedEventArgs e)
    {
        viewModel.Models.Mode = ModelSelectionMode.Automatic;
        viewModel.RefreshNavigationState();
    }

    private void OnModelModeSkip(object sender, RoutedEventArgs e)
    {
        viewModel.Models.Mode = ModelSelectionMode.Skip;
        viewModel.RefreshNavigationState();
    }

    private void OnModelModeManual(object sender, RoutedEventArgs e)
    {
        viewModel.Models.Mode = ModelSelectionMode.Manual;
        viewModel.RefreshNavigationState();
    }

    private void OnManualModelIdChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        viewModel.Models.ManualModelId = textBox.Text;
        viewModel.RefreshNavigationState();
    }

    private void OnManualContextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (int.TryParse(textBox.Text, out var context))
        {
            viewModel.Models.ManualContextWindow = context;
            viewModel.RefreshNavigationState();
        }
    }

    private void OnAgentChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        if (comboBox.Tag is not string agent)
        {
            return;
        }

        if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is AgentChoice choice)
        {
            viewModel.Agents.SetChoice(agent, choice);
            viewModel.RefreshNavigationState();
        }
    }

    private void OnReviewConfirmedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            viewModel.SetReviewConfirmed(checkBox.IsChecked == true);
        }
    }
}
