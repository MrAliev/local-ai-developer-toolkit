using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// #209/m4: Execute is async void — the ICommand contract leaves no other shape — so an
/// exception escaping it lands on the WPF dispatcher and kills the installer with no
/// explanation. With a sink the command reports and stays usable; the wizard exposes the
/// report as an error state instead of dying.
/// </summary>
public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task A_failing_command_reports_to_the_sink_and_can_run_again()
    {
        Exception? seen = null;
        var reported = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            () => Task.FromException(new InvalidOperationException("boom")),
            canExecute: null,
            onError: exception =>
            {
                seen = exception;
                reported.TrySetResult();
            });

        command.Execute(null);
        await reported.Task;
        await Task.Yield();

        Assert.IsType<InvalidOperationException>(seen);
        Assert.True(command.CanExecute(null), "the failed run must release the re-entry latch");
    }

    [Fact]
    public void The_wizard_exposes_a_reported_error_instead_of_dying()
    {
        var wizard = new InstallerWizardViewModel();
        var raised = new List<string?>();
        wizard.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        wizard.ReportUnexpectedError(new InvalidOperationException("boom"));

        Assert.True(wizard.HasUnexpectedError);
        Assert.Contains("boom", wizard.UnexpectedError, StringComparison.Ordinal);
        Assert.Contains(nameof(wizard.UnexpectedError), raised);
        Assert.Contains(nameof(wizard.HasUnexpectedError), raised);
    }
}
