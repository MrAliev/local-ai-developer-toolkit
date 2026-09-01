using System.Collections.ObjectModel;
using System.IO;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.ViewModels;

/// <summary>What the person came here to do. One executable, four errands.</summary>
public enum StartChoice
{
    Install,

    /// <summary>Put the current release in place over what is already there, keeping everything.</summary>
    UpdateOrRepair,

    /// <summary>
    /// Clear the binaries and the transient state, keep the indexes and settings a fresh
    /// install would honour, and install again. This is the reinstall-friendly row of the
    /// removal matrix followed by an installation — not a separate mechanism.
    /// </summary>
    CleanReinstall,

    Remove,
}

/// <summary>
/// One option on the start page: what it does, and — when it cannot be done here — why not.
/// A greyed-out button with no explanation is a worse answer than no button at all.
/// </summary>
public sealed record StartActionOption(
    StartChoice Choice,
    string Title,
    string Description,
    bool IsAvailable,
    string UnavailableReason)
{
    public bool IsUnavailable => !IsAvailable;
}

/// <summary>
/// The first thing the installer shows, because it is also the uninstaller, the updater and
/// the repair tool. Which of those it can be depends on what is already on the machine, so it
/// looks first and offers second.
/// </summary>
public sealed class InstallerStartViewModel : ObservableObject
{
    private readonly ExistingLocalAiSnapshot existing;

    /// <summary>
    /// Which release is installed, as opposed to which directory holds it. The inspector reports
    /// the directory from the pointer and never asks — so this screen said
    /// "LocalAi 467ed5f0f9bf is installed" while the next window, doctor and update all said
    /// 0.1.51, each reading the release record this one did not.
    /// </summary>
    private readonly InstalledVersion installed;

    public InstallerStartViewModel(
        string? localAppData = null,
        IExistingLocalAiInspector? inspector = null,
        Func<InstalledVersion>? readInstalledVersion = null)
    {
        var root = localAppData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        existing = (inspector ?? new ExistingLocalAiInspector(new SystemFileSystemProbe()))
            .Inspect(root);
        installed = readInstalledVersion is null
            ? InstalledVersionReader.Read(Path.Combine(root, "LocalAi"))
            : readInstalledVersion();
        foreach (var option in BuildOptions())
        {
            Actions.Add(option);
        }
    }

    public ObservableCollection<StartActionOption> Actions { get; } = [];

    public ExistingLocalAiState State => existing.State;

    public string? InstalledVersion => existing.Version;

    /// <summary>
    /// The release this installation came from, with a leading space, or nothing when it did
    /// not record one. The build id is not a substitute: it answers a question nobody has asked
    /// yet, in the sentence that has to be legible at a glance.
    /// </summary>
    private string Release =>
        installed.ReleaseVersion is { Length: > 0 } release ? " " + release : string.Empty;

    public string Headline => existing.State switch
    {
        ExistingLocalAiState.Compatible =>
            "LocalAi" + Release + " is installed on this computer.",
        ExistingLocalAiState.Unrecognized =>
            "There is a LocalAi directory here, but it is not a working installation.",
        _ => "LocalAi is not installed on this computer.",
    };

    public string Detail => existing.State switch
    {
        // The build id lives here rather than in the headline, and only when there is no
        // release to name: somebody has to be able to answer "which one are you running", and
        // an unlabelled hash beside the product name is what read as a version.
        ExistingLocalAiState.Compatible when installed.ReleaseVersion is null &&
            installed.VersionDirectory is { Length: > 0 } build =>
            $"Build {build}. This installation does not record which release it came from. " +
            "Choose what to do with it. Nothing is changed until you confirm it on a review " +
            "page.",
        ExistingLocalAiState.Compatible =>
            "Choose what to do with it. Nothing is changed until you confirm it on a review " +
            "page.",
        ExistingLocalAiState.Unrecognized =>
            (existing.Reason ?? "The installation could not be read.") +
            " Installing again repairs it; removing clears it away.",
        _ => "Install it to give your assistants local code search and local models.",
    };

    public bool HasProblem => existing.State == ExistingLocalAiState.Unrecognized;

    /// <summary>The preset the removal wizard opens on for each errand that goes there.</summary>
    public static RemovalPreset PresetFor(StartChoice choice) =>
        choice == StartChoice.CleanReinstall
            ? RemovalPreset.ReinstallFriendly
            : RemovalPreset.FullUninstall;

    public StartActionOption Option(StartChoice choice) =>
        Actions.Single(action => action.Choice == choice);

    private IEnumerable<StartActionOption> BuildOptions()
    {
        var installed = existing.State != ExistingLocalAiState.Absent;
        // The release, not the directory: the row says why Install is off, and "already
        // installed" is the answer — naming a build id there repeats the headline's old
        // mistake in smaller type.
        var version = Release;
        yield return new StartActionOption(
            StartChoice.Install,
            "Install LocalAi",
            "Sets up the prerequisites, the runtime and the client integrations.",
            !installed,
            existing.State == ExistingLocalAiState.Compatible
                ? "LocalAi" + version + " is already installed — use Update or repair."
                : "There is already a LocalAi directory here — use Update or repair, which " +
                    "installs over it.");
        yield return new StartActionOption(
            StartChoice.UpdateOrRepair,
            installed && existing.State == ExistingLocalAiState.Unrecognized
                ? "Repair this installation"
                : "Update or repair",
            // Not "the release you choose": this path folds the release page away and resolves
            // it behind the first screen. The sentence advertised a question the wizard
            // deliberately stopped asking, which leaves the reader waiting for it.
            "Installs the current release over the one that is there. Indexes, settings and " +
            "client integrations are kept.",
            installed,
            "Nothing is installed to update.");
        yield return new StartActionOption(
            StartChoice.CleanReinstall,
            "Clean reinstall",
            "Removes the binaries and the transient state, keeps the repository indexes and " +
            "the settings a fresh install would honour, then installs again.",
            installed,
            "Nothing is installed to reinstall.");
        yield return new StartActionOption(
            StartChoice.Remove,
            "Remove LocalAi",
            "Choose what goes and what stays, then take it off this computer.",
            installed,
            "Nothing is installed to remove.");
    }
}
