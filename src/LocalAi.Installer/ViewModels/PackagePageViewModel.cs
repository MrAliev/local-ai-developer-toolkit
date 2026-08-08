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
    public const string LatestTag = "latest";

    private string releaseVersion = LatestTag;
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
            Resolved = null;
            ResolvedTag = null;
            OnPropertyChanged(nameof(ResolvedTag));
            OnPropertyChanged(nameof(WantsLatest));
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
        PackageSourceState.Selected => $"LocalAi package: {ResolvedTag ?? ReleaseVersion}",
        PackageSourceState.Incompatible =>
            $"LocalAi package: {ResolvedTag ?? ReleaseVersion} is not compatible — " +
            "it will not be installed",
        _ => "LocalAi package: not resolved — it will not be installed",
    };

    /// <summary>
    /// The verified release, kept so the install step uses exactly the manifest that was
    /// checked here instead of fetching and trusting a second copy.
    /// </summary>
    public ResolvedRelease? Resolved { get; private set; }

    /// <summary>
    /// True while the field still asks for whatever is newest instead of naming one release.
    /// </summary>
    public bool WantsLatest =>
        string.Equals(releaseVersion.Trim(), LatestTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tag that was actually resolved, so a request left at "latest" can still name the
    /// real release without the request itself being replaced by it.
    /// </summary>
    public string? ResolvedTag { get; private set; }

    /// <summary>
    /// Records the resolved, verified release — and leaves the request alone.
    ///
    /// This used to write the resolved tag back into <see cref="ReleaseVersion"/>, so "latest"
    /// became "0.1.29" the moment it was first checked. From then on the field named one
    /// specific release and every later check asked for that one: a wizard opened before a
    /// release and used after it kept installing the version that had been newest when it first
    /// looked, while still appearing to track the newest. "Latest" is a standing request, not a
    /// value to be resolved once and overwritten.
    /// </summary>
    public void SelectResolvedRelease(ResolvedRelease release, string tag)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        Resolved = release;
        ResolvedTag = tag;
        OnPropertyChanged(nameof(ResolvedTag));
        OnPropertyChanged(nameof(WantsLatest));
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
        releaseVersion = LatestTag;
        Resolved = null;
        ResolvedTag = null;
        OnPropertyChanged(nameof(ReleaseVersion));
        OnPropertyChanged(nameof(ResolvedTag));
        OnPropertyChanged(nameof(WantsLatest));
        StatusText = "No release has been checked yet.";
        State = PackageSourceState.NotChecked;
    }
}
