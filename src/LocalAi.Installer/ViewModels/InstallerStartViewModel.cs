using System.Collections.ObjectModel;
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

    public InstallerStartViewModel(
        string? localAppData = null,
        IExistingLocalAiInspector? inspector = null)
    {
        var root = localAppData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        existing = (inspector ?? new ExistingLocalAiInspector(new SystemFileSystemProbe()))
            .Inspect(root);
        foreach (var option in BuildOptions())
        {
            Actions.Add(option);
        }
    }

    public ObservableCollection<StartActionOption> Actions { get; } = [];

    public ExistingLocalAiState State => existing.State;

    public string? InstalledVersion => existing.Version;

    public string Headline => existing.State switch
    {
        ExistingLocalAiState.Compatible =>
            "LocalAi " + existing.Version + " is installed on this computer.",
        ExistingLocalAiState.Unrecognized =>
            "There is a LocalAi directory here, but it is not a working installation.",
        _ => "LocalAi is not installed on this computer.",
    };

    public string Detail => existing.State switch
    {
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
        var version = existing.Version is { Length: > 0 } named ? " " + named : string.Empty;
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
            "Installs the release you choose over the current one. Indexes, settings and " +
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
