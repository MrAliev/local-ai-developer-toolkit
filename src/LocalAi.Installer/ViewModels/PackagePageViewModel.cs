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
    private string sourceFolder = string.Empty;
    private PackageSourceState state = PackageSourceState.NotChecked;
    private string statusText =
        "No release has been checked yet.";

    public string ReleaseVersion
    {
        get => releaseVersion;
        set
        {
            SetProperty(ref releaseVersion, value);
            // Editing the tag invalidates whatever was resolved for the previous one, and
            // abandons any resolve still running for it — otherwise the rail keeps saying a
            // check is in flight for a question nobody is asking any more.
            IsResolving = false;
            Resolved = null;
            ResolvedTag = null;
            OnPropertyChanged(nameof(ResolvedTag));
            OnPropertyChanged(nameof(WantsLatest));
            State = PackageSourceState.NotChecked;
            StatusText = "No release has been checked yet.";
        }
    }

    /// <summary>
    /// A folder holding the three files a release publishes, for a machine with no route to
    /// GitHub. Empty means the release is fetched from GitHub, which is what almost every
    /// installation does.
    ///
    /// Reading from a folder changes where the bytes came from, not whether they are believed:
    /// the manifest is still checked against the embedded key and the package against the hash
    /// inside it.
    /// </summary>
    public string SourceFolder
    {
        get => sourceFolder;
        set
        {
            SetProperty(ref sourceFolder, value ?? string.Empty);
            // Changing where a release comes from invalidates whatever the last one resolved
            // to, and abandons any resolve still running for the old source.
            IsResolving = false;
            Resolved = null;
            ResolvedTag = null;
            OnPropertyChanged(nameof(ResolvedTag));
            State = PackageSourceState.NotChecked;
            StatusText = "No release has been checked yet.";
        }
    }

    /// <summary>
    /// Offers the folder the installer was started from, when it holds a release. That is what
    /// makes "download it once and pass the folder on" work without the person at the far end
    /// having to type a path — and it is only ever an offer, because a folder that merely has
    /// the right three file names still has to pass verification.
    /// </summary>
    public void OfferLocalFolder(string? installerDirectory)
    {
        if (sourceFolder.Length == 0 &&
            DirectoryReleaseFeed.LooksLikeReleaseFolder(installerDirectory))
        {
            SourceFolder = installerDirectory!;
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

    /// <summary>
    /// The version directory already active on this machine, when there is one.
    ///
    /// Used only to say so before the run starts. The installer handles the case correctly —
    /// it reports <c>AlreadyInstalled</c> and changes nothing — but it reports it afterwards,
    /// in a line of the finish log, so a run that was never going to change anything looks
    /// exactly like one that did until it is over.
    /// </summary>
    public string? InstalledVersionDirectory { get; set; }

    /// <summary>
    /// True when the resolved release is the one already installed.
    /// </summary>
    public bool IsAlreadyInstalled =>
        Resolved is { } resolved &&
        !string.IsNullOrWhiteSpace(InstalledVersionDirectory) &&
        string.Equals(
            resolved.Manifest.VersionDirectory,
            InstalledVersionDirectory,
            StringComparison.OrdinalIgnoreCase);

    public string ReviewText => State switch
    {
        PackageSourceState.Selected when IsAlreadyInstalled =>
            $"LocalAi package: {ResolvedTag ?? ReleaseVersion} is already installed — " +
            "nothing will change",
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
    /// <summary>
    /// Whether a release is being resolved right now.
    ///
    /// "Nobody has asked yet", "asking" and "asked and got nothing" were one state, so the
    /// step rail could not tell a check in flight from a check that failed — and said
    /// "checking…" forever on a path where nothing had asked.
    /// </summary>
    public bool IsResolving { get; private set; }

    /// <summary>Says a resolve has started; every terminal outcome below clears it.</summary>
    public void BeginResolving()
    {
        IsResolving = true;
        Resolved = null;
        ResolvedTag = null;
        OnPropertyChanged(nameof(ResolvedTag));
        OnPropertyChanged(nameof(WantsLatest));
        StatusText = "Checking the release…";
        State = PackageSourceState.NotChecked;
    }

    public void SelectResolvedRelease(ResolvedRelease release, string tag)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        IsResolving = false;
        Resolved = release;
        ResolvedTag = tag;
        OnPropertyChanged(nameof(ResolvedTag));
        OnPropertyChanged(nameof(WantsLatest));
        OnPropertyChanged(nameof(IsAlreadyInstalled));
        StatusText = IsAlreadyInstalled
            ? $"Release {tag} is already installed. Continuing will re-run the other steps " +
              "and leave the LocalAi version untouched."
            : $"Release {tag} verified, " +
              $"{release.Manifest.PackageSize / (1024d * 1024):N0} MB to download.";
        State = PackageSourceState.Selected;
    }

    public void ReportIncompatible(string reason)
    {
        IsResolving = false;
        Resolved = null;
        StatusText = reason;
        State = PackageSourceState.Incompatible;
    }

    public void ReportUnavailable(string reason)
    {
        IsResolving = false;
        Resolved = null;
        StatusText = reason;
        State = PackageSourceState.Unavailable;
    }

    public void Reset()
    {
        IsResolving = false;
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
