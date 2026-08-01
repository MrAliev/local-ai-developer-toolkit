using LocalAi.Installer.Core.Releases;

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

    /// <summary>
    /// The verified release, kept so the install step uses exactly the manifest that was
    /// checked here instead of fetching and trusting a second copy.
    /// </summary>
    public ResolvedRelease? Resolved { get; private set; }

    /// <summary>
    /// Reports the tag that was actually resolved, so a field left at "latest" names the
    /// real release instead of leaving the user to guess which one was chosen.
    /// </summary>
    public void SelectResolvedRelease(ResolvedRelease release, string tag)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        Resolved = release;
        releaseVersion = tag;
        OnPropertyChanged(nameof(ReleaseVersion));
        StatusText =
            $"Release {tag} verified, " +
            $"{release.Manifest.PackageSize / (1024d * 1024):N0} MB to download.";
        State = PackageSourceState.Selected;
    }

    public void ReportIncompatible(string reason)
    {
        Resolved = null;
        StatusText = reason;
        State = PackageSourceState.Incompatible;
    }

    public void ReportUnavailable(string reason)
    {
        Resolved = null;
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
