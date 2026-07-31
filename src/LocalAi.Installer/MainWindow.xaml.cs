using System.Windows;

namespace LocalAi.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerWizardViewModel viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnPreviousClicked(object sender, RoutedEventArgs e)
    {
        viewModel.MovePrevious();
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        viewModel.MoveNext();
    }

    private void OnRunClicked(object sender, RoutedEventArgs e)
    {
    }
}
