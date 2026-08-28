namespace LocalAi.Installer.ViewModels;

public sealed class FinishPageViewModel : ObservableObject
{
    private bool success;
    private bool requiresRestart;
    private string? summary;
    private string? runLog;
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

    public string? Summary
    {
        get => summary;
        set
        {
            SetProperty(ref summary, value);
        }
    }

    /// <summary>
    /// What the run did, line by line. It was called RollbackNotes, which promised an
    /// installation that could be undone; there has never been one, and the value has
    /// always been the log.
    /// </summary>
    public string? RunLog
    {
        get => runLog;
        set
        {
            SetProperty(ref runLog, value);
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
