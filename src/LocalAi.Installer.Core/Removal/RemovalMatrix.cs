namespace LocalAi.Installer.Core.Removal;

/// <summary>
/// One row of the removal matrix: a thing an uninstall can take away, chosen on its own.
/// </summary>
public enum RemovalItem
{
    /// <summary>Binaries, the stable launcher, the version pointer. Removing these is the uninstall.</summary>
    Binaries,

    /// <summary>Repository indexes and their branch overlays: hours of embedding to rebuild.</summary>
    RepositoryIndexes,

    /// <summary>The hand-tuned runtime settings a reinstall would otherwise honour.</summary>
    Settings,

    /// <summary>Queue, archive, staging, quarantine, telemetry: state nothing outlives.</summary>
    TransientState,

    /// <summary>
    /// Whatever else the runtime root holds. The root is shared with the runtime, which writes
    /// loose files the installer neither reads nor names, so a matrix of only known entries
    /// would leave a full uninstall quietly incomplete. Everything unrecognised is listed by
    /// path on the review page instead of being assumed harmless.
    /// </summary>
    OtherRuntimeFiles,

    /// <summary>Claude's server registrations and its managed instructions block.</summary>
    ClaudeIntegration,

    /// <summary>Codex's server registrations, their tool sub-tables, and its managed block.</summary>
    CodexIntegration,

    /// <summary>The chained Git hook dispatchers, per connected repository.</summary>
    GitHooks,

    /// <summary>
    /// The release signing key directory. Kept unless separately confirmed: removing it makes
    /// the offline backup the only copy that exists.
    /// </summary>
    SigningKeys,
}

/// <summary>Prefilled sets of choices. A preset is checkboxes, never a different plan.</summary>
public enum RemovalPreset
{
    FullUninstall,

    /// <summary>Stop the agents using LocalAi without losing the runtime.</summary>
    DisconnectClients,

    /// <summary>Clear the binaries, keep what an hour of embedding built.</summary>
    ReinstallFriendly,
}

public enum RemovalDisposition
{
    Remove,
    Keep,

    /// <summary>
    /// The preset takes no position and the person has to. Prefilled as kept — an uninstall
    /// may never remove something by default that the person was supposed to decide on.
    /// </summary>
    Ask,
}

/// <summary>
/// What each preset proposes for each row, and what each row means.
///
/// The matrix is the contract between the review page and the planner: the page renders these
/// rows, the planner turns the chosen ones into paths, and the presets do nothing but fill the
/// checkboxes in. Keeping the three of them in one table is what makes "a preset is just
/// prefilled checkboxes" a property that can be tested rather than a claim in a comment.
/// </summary>
public static class RemovalMatrix
{
    public static IReadOnlyList<RemovalItem> Items { get; } = Array.AsReadOnly(
        Enum.GetValues<RemovalItem>());

    public static IReadOnlyList<RemovalPreset> Presets { get; } = Array.AsReadOnly(
        Enum.GetValues<RemovalPreset>());

    /// <summary>
    /// The runtime settings files, by name. They live loose in the runtime root beside the
    /// state, and they are the reason "reinstall-friendly" exists: each one is hand-tuned, and
    /// a fresh installation reads whichever of them it finds.
    /// </summary>
    public static IReadOnlyList<string> SettingsFileNames { get; } = Array.AsReadOnly(
    [
        "policy.json",
        "retention.json",
        "log-triage.json",
        "language-servers.json",
        "semantic-indexing.json",
    ]);

    /// <summary>The directory holding the release signing keys, relative to the runtime root.</summary>
    public const string SigningKeyDirectoryName = "release-signing";

    /// <summary>
    /// Where the installer's own record lives — deliberately outside the runtime root, and
    /// never removed. It is the record of what installs did to this machine, including the
    /// uninstall that is running now.
    /// </summary>
    public const string JournalDirectoryName = "LocalAi-installer-logs";

    public static RemovalDisposition Disposition(RemovalPreset preset, RemovalItem item) =>
        preset switch
        {
            RemovalPreset.FullUninstall => item switch
            {
                RemovalItem.SigningKeys => RemovalDisposition.Ask,
                _ => RemovalDisposition.Remove,
            },
            RemovalPreset.DisconnectClients => item switch
            {
                RemovalItem.ClaudeIntegration or
                RemovalItem.CodexIntegration or
                RemovalItem.GitHooks => RemovalDisposition.Remove,
                _ => RemovalDisposition.Keep,
            },
            RemovalPreset.ReinstallFriendly => item switch
            {
                RemovalItem.Binaries or
                RemovalItem.TransientState or
                RemovalItem.OtherRuntimeFiles => RemovalDisposition.Remove,
                RemovalItem.RepositoryIndexes or
                RemovalItem.Settings or
                RemovalItem.SigningKeys => RemovalDisposition.Keep,
                _ => RemovalDisposition.Ask,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };

    public static string Title(RemovalItem item) =>
        item switch
        {
            RemovalItem.Binaries => "Binaries, launcher, version pointer",
            RemovalItem.RepositoryIndexes => "Repository indexes and overlays",
            RemovalItem.Settings => "Settings files",
            RemovalItem.TransientState => "Queue, archive, quarantine, telemetry",
            RemovalItem.OtherRuntimeFiles => "Everything else in the runtime root",
            RemovalItem.ClaudeIntegration => "Claude integration",
            RemovalItem.CodexIntegration => "Codex integration",
            RemovalItem.GitHooks => "Git hook dispatchers",
            RemovalItem.SigningKeys => "Release signing keys",
            _ => throw new ArgumentOutOfRangeException(nameof(item), item, null),
        };

    public static string Note(RemovalItem item) =>
        item switch
        {
            RemovalItem.Binaries => "Removing these is the uninstall.",
            RemovalItem.RepositoryIndexes =>
                "Hours of embedding to rebuild; keeping them makes a reinstall pick the " +
                "repositories back up at once.",
            RemovalItem.Settings => "Hand-tuned; a reinstall honours them.",
            RemovalItem.TransientState => "Transient state, nothing to preserve.",
            RemovalItem.OtherRuntimeFiles =>
                "Anything in the runtime root this installer does not recognise, listed by path.",
            RemovalItem.ClaudeIntegration =>
                "Server registrations and the managed block; your own text is untouched.",
            RemovalItem.CodexIntegration =>
                "Server registrations, their tool sub-tables, and the managed block.",
            RemovalItem.GitHooks =>
                "Listed from the runtime manifests, each repository its own choice.",
            RemovalItem.SigningKeys =>
                "Kept unless separately confirmed: the offline backup then becomes the only copy.",
            _ => throw new ArgumentOutOfRangeException(nameof(item), item, null),
        };

    public static string Title(RemovalPreset preset) =>
        preset switch
        {
            RemovalPreset.FullUninstall => "Full uninstall",
            RemovalPreset.DisconnectClients => "Disconnect clients",
            RemovalPreset.ReinstallFriendly => "Reinstall-friendly",
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };

    public static string Description(RemovalPreset preset) =>
        preset switch
        {
            RemovalPreset.FullUninstall =>
                "Everything LocalAi installed goes, except the signing keys unless you " +
                "confirm those separately.",
            RemovalPreset.DisconnectClients =>
                "The agents stop using LocalAi; the runtime stays exactly as it is.",
            RemovalPreset.ReinstallFriendly =>
                "The binaries go; the indexes and settings a reinstall would honour stay.",
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };

    /// <summary>
    /// Which row one top-level entry of the runtime root belongs to. Anything this does not
    /// recognise is <see cref="RemovalItem.OtherRuntimeFiles"/> rather than silently kept.
    /// </summary>
    public static RemovalItem ClassifyRootEntry(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "installer", StringComparison.OrdinalIgnoreCase))
        {
            // The installer directory holds the launcher backups an activation parks there,
            // which are worth exactly as much as the binaries they came from.
            return RemovalItem.Binaries;
        }

        if (string.Equals(name, "repositories", StringComparison.OrdinalIgnoreCase))
        {
            return RemovalItem.RepositoryIndexes;
        }

        if (string.Equals(name, SigningKeyDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return RemovalItem.SigningKeys;
        }

        if (SettingsFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return RemovalItem.Settings;
        }

        return TransientNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? RemovalItem.TransientState
            : RemovalItem.OtherRuntimeFiles;
    }

    /// <summary>
    /// The broker's working state. Named rather than inferred, because the distinction that
    /// matters to a person clearing space — "this rebuilds itself" against "this took an hour
    /// of GPU time" — is not visible from a directory name.
    /// </summary>
    private static readonly string[] TransientNames =
    [
        "jobs",
        "archive",
        "staging",
        "quarantine",
        "telemetry",
        "host.json",
        "broker.lock",
        "sequence.json",
        "shutdown.request",
    ];
}
