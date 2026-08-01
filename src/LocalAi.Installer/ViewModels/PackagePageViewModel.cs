namespace LocalAi.Installer.ViewModels;

public sealed class PackagePageViewModel : ObservableObject
{
    private string releaseVersion = "latest";
    private bool isCompatible;
    private bool hasPackage;

    public string ReleaseVersion
    {
        get => releaseVersion;
        set
        {
            SetProperty(ref releaseVersion, value);
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public bool IsCompatible
    {
        get => isCompatible;
        set
        {
            SetProperty(ref isCompatible, value);
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public bool HasPackage
    {
        get => hasPackage;
        set
        {
            SetProperty(ref hasPackage, value);
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public bool CanContinue => HasPackage && IsCompatible;

    public void SelectCompatibleRelease(string version, bool compatible = true)
    {
        ReleaseVersion = version;
        IsCompatible = compatible;
        HasPackage = true;
    }

    public void Reset()
    {
        ReleaseVersion = "latest";
        IsCompatible = false;
        HasPackage = false;
    }
}
