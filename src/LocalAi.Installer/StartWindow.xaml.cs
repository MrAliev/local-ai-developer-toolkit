using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.ViewModels;
using LocalAi.Installer.Core;
using System.Linq;
using System.Windows.Input;

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
        // The caption is the desktop manager's, not the palette's.
        DarkCaption.Follow(this);
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

    private void OnChooseTheme(object sender, RoutedEventArgs e)
    {
        // Checked fires while the window is being built, as each binding settles, and again
        // when the person clicks. Writing the value it already holds is harmless; writing a
        // different one before the view model exists would not be.
        if (sender is RadioButton { Tag: string name } &&
            Enum.TryParse<InstallerTheme>(name, out var theme) &&
            theme != viewModel.Theme)
        {
            viewModel.ChooseTheme(theme);
        }
    }

    private void OnChooseErrand(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: StartChoice choice })
        {
            viewModel.Select(choice);
        }
    }

    /// <summary>
    /// Stock WPF moves focus with the arrow keys without moving the selection, so a group of
    /// radio buttons would leave the person's choice behind their focus. Disabled rows are not
    /// focusable, so an unavailable errand is skipped without being named here.
    /// </summary>
    private void OnErrandKeyDown(object sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Down or Key.Right => 1,
            Key.Up or Key.Left => -1,
            _ => 0,
        };
        if (step == 0)
        {
            return;
        }

        var reachable = viewModel.Actions.Where(option => option.IsAvailable).ToArray();
        if (reachable.Length == 0)
        {
            return;
        }

        var current = Array.FindIndex(reachable, option => option.IsSelected);
        var next = current < 0
            ? (step > 0 ? 0 : reachable.Length - 1)
            : ((current + step) % reachable.Length + reachable.Length) % reachable.Length;
        viewModel.Select(reachable[next].Choice);
        e.Handled = true;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (viewModel.Selected is not { } choice)
        {
            return;
        }

        Open(choice switch
        {
            // The errand travels with the window: an update that asks every question an
            // install asks is a wizard that did not hear the answer already given (#257).
            StartChoice.Install or StartChoice.UpdateOrRepair => new MainWindow(choice, canReturnToStart: true),
            // The removal half opens on the reinstall-friendly row and offers the install half
            // when it finishes; two deliberate wizards, because the install has prerequisites
            // and a release choice of its own to confirm.
            StartChoice.CleanReinstall => new UninstallWindow(
                InstallerStartViewModel.PresetFor(choice),
                offersInstallAfterwards: true,
                canReturnToStart: true),
            _ => new UninstallWindow(
                InstallerStartViewModel.PresetFor(choice),
                canReturnToStart: true),
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Hidden rather than closed, so Back from the wizard's first page finds this screen
    /// exactly as it was left — the errand still chosen, the language and theme untouched.
    /// Rebuilding it instead would mean carrying that state out and back through a
    /// constructor, and losing whatever was forgotten.
    ///
    /// A wizard closed any other way — the caption's X, Cancel, a finished run — takes this
    /// screen with it, or the process would linger behind an invisible window.
    /// </summary>
    private void Open(Window window)
    {
        var returning = false;
        if (window is IReturnsToStart wizard)
        {
            wizard.ReturnToStart += (_, _) => returning = true;
        }

        window.Closed += (_, _) =>
        {
            if (returning)
            {
                Application.Current.MainWindow = this;
                Show();
                Activate();
            }
            else
            {
                Close();
            }
        };

        Application.Current.MainWindow = window;
        window.Show();
        Hide();
    }
}
