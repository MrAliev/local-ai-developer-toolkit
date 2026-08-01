namespace LocalAi.Installer.ViewModels;

public enum PackageSourceState
{
    NotChecked,
    Unavailable,
    Selected,
    Incompatible,
}

/// <summary>
/// Chooses the LocalAi package to install.
///
/// The previous version reported "compatible" for whatever string was typed, without ever
/// contacting a release feed, so it always looked like a package had been found. This one
/// reports only what it actually knows and never claims a package it has not resolved.
/// </summary>
public sealed class PackagePageViewModel : ObservableObject
{
    private string releaseVersion = "latest";
    private PackageSourceState state = PackageSourceState.NotChecked;
    private string statusText =
        "No release has been checked yet.";

    public string ReleaseVersion
    {
        get => releaseVersion;
        set
        {
            SetProperty(ref releaseVersion, value);
            // Editing the tag invalidates whatever was resolved for the previous one.
            State = PackageSourceState.NotChecked;
            StatusText = "No release has been checked yet.";
        }
    }

    public PackageSourceState State
    {
        get => state;
        private set
        {
            SetProperty(ref state, value);
            OnPropertyChanged(nameof(HasPackage));
            OnPropertyChanged(nameof(IsCompatible));
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(ReviewText));
        }
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool HasPackage => State == PackageSourceState.Selected;

    public bool IsCompatible => State == PackageSourceState.Selected;

    /// <summary>
    /// An unresolved package does not block the wizard: dependencies, models and client
    /// integration are still worth installing on their own. The confirmation page states
    /// plainly that the LocalAi package itself will not be installed.
    /// </summary>
    public bool CanContinue => true;

    public string ReviewText => State switch
    {
        PackageSourceState.Selected => $"LocalAi package: {ReleaseVersion}",
        PackageSourceState.Incompatible =>
            $"LocalAi package: {ReleaseVersion} is not compatible — it will not be installed",
        _ => "LocalAi package: not resolved — it will not be installed",
    };

    public void SelectResolvedRelease(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        releaseVersion = version;
        OnPropertyChanged(nameof(ReleaseVersion));
        StatusText = $"Release {version} resolved and verified.";
        State = PackageSourceState.Selected;
    }

    public void ReportIncompatible(string reason)
    {
        StatusText = reason;
        State = PackageSourceState.Incompatible;
    }

    public void ReportUnavailable(string reason)
    {
        StatusText = reason;
        State = PackageSourceState.Unavailable;
    }

    public void Reset()
    {
        releaseVersion = "latest";
        OnPropertyChanged(nameof(ReleaseVersion));
        StatusText = "No release has been checked yet.";
        State = PackageSourceState.NotChecked;
    }
}
