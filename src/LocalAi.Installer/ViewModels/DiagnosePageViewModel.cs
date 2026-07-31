namespace LocalAi.Installer.ViewModels;

public sealed class DiagnosePageViewModel : ObservableObject
{
    private bool isSupported;
    private string? unsupportedReason;

    public bool IsSupported
    {
        get => isSupported;
        set
        {
            SetProperty(ref isSupported, value);
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public string? UnsupportedReason
    {
        get => unsupportedReason;
        set
        {
            SetProperty(ref unsupportedReason, value);
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public bool CanContinue => IsSupported;

    public void SetResult(bool supported, string? reason = null)
    {
        IsSupported = supported;
        UnsupportedReason = supported ? null : reason;
    }
}
