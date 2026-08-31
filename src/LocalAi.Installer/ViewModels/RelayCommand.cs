using System.Windows.Input;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Minimal command so navigation lives in the view model instead of Click handlers.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action execute =
        execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async command that refuses re-entry while running, so a second click on Install cannot
/// start a second transaction.
///
/// Execute is async void — the ICommand contract leaves no other shape — so an exception
/// escaping it lands on the dispatcher and kills the whole installer (#209/m4). A command
/// constructed with an error sink routes the exception there instead; without a sink the
/// old crash behavior stands, because silently swallowing would be worse.
/// </summary>
public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onError = null)
    : ICommand
{
    private readonly Func<Task> execute =
        execute ?? throw new ArgumentNullException(nameof(execute));

    private bool isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        catch (Exception exception) when (onError is not null)
        {
            onError(exception);
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
