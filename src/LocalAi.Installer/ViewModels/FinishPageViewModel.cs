namespace LocalAi.Installer.ViewModels;

public sealed class FinishPageViewModel : ObservableObject
{
    private bool success;
    private bool requiresRestart;
    private string? rollbackNotes;
    private string? progress;

    public bool Success
    {
        get => success;
        set
        {
            SetProperty(ref success, value);
        }
    }

    public bool RequiresRestart
    {
        get => requiresRestart;
        set
        {
            SetProperty(ref requiresRestart, value);
            OnPropertyChanged(nameof(RestartNotice));
        }
    }

    public string? RollbackNotes
    {
        get => rollbackNotes;
        set
        {
            SetProperty(ref rollbackNotes, value);
        }
    }

    public string? Progress
    {
        get => progress;
        set
        {
            SetProperty(ref progress, value);
        }
    }

    public string RestartNotice => RequiresRestart ? "Restart required." : "No restart needed.";
}
