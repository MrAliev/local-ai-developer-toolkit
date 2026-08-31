using System.Text;
using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Removal;

/// <summary>One path an uninstall would delete, and the matrix row that put it there.</summary>
public sealed record RemovalPathEntry(RemovalItem Item, string Path, bool IsDirectory);

/// <summary>
/// What removing the hooks from one connected repository would do — or why it would not.
///
/// A repository that has moved or been deleted since it was connected is not a failure and
/// not something to retry: it is named, skipped, and reported, because the alternative is an
/// uninstall that stops halfway through somebody else's machine.
/// </summary>
public sealed record HookRemovalEntry(
    string RepositoryId,
    string CommonDirectory,
    string? HooksDirectory,
    IReadOnlyList<string> Dispatchers,
    IReadOnlyList<string> RestoredHooks,
    IReadOnlyList<string> ExcludePatterns,
    string? ExcludePath,
    string? SkipReason)
{
    public bool IsSkipped => SkipReason is not null;

    public bool HasWork =>
        !IsSkipped && (Dispatchers.Count > 0 || ExcludePatterns.Count > 0);
}

/// <summary>
/// Something an uninstall deliberately does not touch, said out loud rather than left for the
/// person to discover. Prerequisites installed through winget are machine-wide and other
/// software may use them; models may serve other tools; the installer's own journal is the
/// record of what installs did.
/// </summary>
public sealed record RetainedNotice(string Title, string Detail);

/// <summary>
/// Everything one uninstall would do, before any of it has been done.
///
/// Same shape as an installation: the whole effect is listed, nothing runs before it has been
/// confirmed, and what the plan names is exactly what apply performs. Client configurations
/// ride along as ordinary <see cref="AgentConfigurationPlan"/>s, so removal inherits the
/// backups, the read-back and the refusal on a concurrent edit that installation already has.
/// </summary>
public sealed record UninstallPlan(
    string RuntimeRoot,
    RemovalSelection Selection,
    IReadOnlyList<RemovalPathEntry> Paths,
    IReadOnlyList<string> RetainedPaths,
    IReadOnlyList<AgentConfigurationPlan> AgentConfigurations,
    IReadOnlyList<HookRemovalEntry> Hooks,
    IReadOnlyList<RetainedNotice> Retained,
    bool RemovesAppsAndFeaturesEntry = false)
{
    public bool HasWork =>
        Paths.Count > 0 ||
        AgentConfigurations.Any(plan => plan.HasChanges) ||
        Hooks.Any(hook => hook.HasWork) ||
        RemovesAppsAndFeaturesEntry;

    /// <summary>
    /// Whether the runtime root would be left with nothing in it.
    ///
    /// Read from what is on disk, not from which boxes are ticked: a full uninstall that keeps
    /// the signing keys — the default — still leaves the root standing, and a finish page that
    /// said otherwise would be wrong on the most common run there is.
    /// </summary>
    public bool RemovesRuntimeRootEntirely => RetainedPaths.Count == 0;

    /// <summary>
    /// The review page's text: every removal named, every skip named, every retention named.
    /// A path that is not in here is a path apply must not touch.
    /// </summary>
    public string PreviewText
    {
        get
        {
            var text = new StringBuilder();
            foreach (var item in RemovalMatrix.Items)
            {
                var paths = Paths.Where(path => path.Item == item).ToArray();
                var agents = AgentPlansFor(item);
                var hooks = item == RemovalItem.GitHooks ? Hooks : [];
                var registration = item == RemovalItem.Binaries && RemovesAppsAndFeaturesEntry;
                if (paths.Length == 0 && agents.Length == 0 && hooks.Count == 0 && !registration)
                {
                    continue;
                }

                text.AppendLine(RemovalMatrix.Title(item) + ":");
                foreach (var path in paths)
                {
                    text.AppendLine("  remove " + path.Path + (path.IsDirectory ? "\\" : string.Empty));
                }

                if (registration)
                {
                    // The entry is what made this removable from Apps & features in the first
                    // place, and the copy it points at is this very executable — so it is named
                    // here, and it goes last.
                    text.AppendLine(
                        "  remove the Apps & features entry, and the uninstaller's own copy last");
                }

                foreach (var file in agents.SelectMany(plan => plan.Files))
                {
                    text.AppendLine("  rewrite " + file.Path);
                }

                foreach (var hook in hooks)
                {
                    text.AppendLine(HookLine(hook));
                    foreach (var dispatcher in hook.Dispatchers)
                    {
                        text.AppendLine("    remove " + dispatcher);
                    }

                    foreach (var restored in hook.RestoredHooks)
                    {
                        text.AppendLine("    restore " + restored);
                    }

                    if (hook.ExcludePatterns.Count > 0 && hook.ExcludePath is not null)
                    {
                        text.AppendLine(
                            "    clean " + hook.ExcludePath + ": " +
                            string.Join(", ", hook.ExcludePatterns));
                    }
                }
            }

            if (!HasWork)
            {
                text.AppendLine("Nothing selected: this run would change nothing.");
            }

            if (RetainedPaths.Count > 0)
            {
                text.AppendLine("Left in " + RuntimeRoot + ":");
                foreach (var path in RetainedPaths)
                {
                    text.AppendLine("  keep " + path);
                }
            }

            foreach (var notice in Retained)
            {
                text.AppendLine("Kept: " + notice.Title + " — " + notice.Detail);
            }

            return text.ToString();
        }
    }

    private AgentConfigurationPlan[] AgentPlansFor(RemovalItem item)
    {
        var agent = item switch
        {
            RemovalItem.ClaudeIntegration => "Claude",
            RemovalItem.CodexIntegration => "Codex",
            _ => null,
        };
        return agent is null
            ? []
            : AgentConfigurations
                .Where(plan =>
                    string.Equals(plan.AgentName, agent, StringComparison.Ordinal) &&
                    plan.HasChanges)
                .ToArray();
    }

    private static string HookLine(HookRemovalEntry hook) =>
        hook.IsSkipped
            ? "  skip " + hook.CommonDirectory + " — " + hook.SkipReason
            : "  " + hook.CommonDirectory +
                (hook.HasWork ? string.Empty : " — no managed dispatchers found");
}
