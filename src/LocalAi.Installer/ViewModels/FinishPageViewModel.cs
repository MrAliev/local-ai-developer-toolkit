using LocalAi.Installer.Core;
﻿namespace LocalAi.Installer.ViewModels;

public sealed class FinishPageViewModel : ObservableObject
{
    private bool success;
    private bool requiresRestart;
    private string? summary;
    private string? runLog;
    private string? progress;
    private string? rollbackReport;

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

    /// <summary>
    /// What rollback actually did, effect by effect: undone, left in place, or failed.
    /// Distinct from <see cref="RunLog"/> on purpose — the log is what the run did, this is
    /// what was taken back, and collapsing the two is how "RollbackNotes" came to mean
    /// nothing.
    /// </summary>
    public string? RollbackReport
    {
        get => rollbackReport;
        set
        {
            SetProperty(ref rollbackReport, value);
            OnPropertyChanged(nameof(HasRollbackReport));
        }
    }

    public bool HasRollbackReport => !string.IsNullOrWhiteSpace(rollbackReport);

    public string RestartNotice => RequiresRestart
        ? InstallerCulture.Pick("Restart required.", "Требуется перезагрузка.")
        : InstallerCulture.Pick("No restart needed.", "Перезагрузка не нужна.");
}
