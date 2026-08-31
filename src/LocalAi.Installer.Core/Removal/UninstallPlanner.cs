using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Agents;
using LocalAi.Repository;

namespace LocalAi.Installer.Core.Removal;

/// <summary>
/// Turns a selection into the exact list of things one uninstall would do.
///
/// It reads and never writes. Everything it produces is meant to be shown before anything is
/// applied, which is also why it enumerates the runtime root rather than reciting a list of
/// paths from memory: the root is shared with the runtime, and a plan built from what is
/// actually there is the only kind that can promise a full uninstall leaves nothing behind.
///
/// The connected repositories come from the runtime manifests under <c>repositories/</c>,
/// which is where every worktree, client and CLI already agree about what a repository is —
/// so the uninstaller lists hooks from the same record that installed them, rather than
/// searching the disk for repositories it has no business scanning.
/// </summary>
public sealed class UninstallPlanner
{
    private readonly InstallationLayout layout;
    private readonly string homeDirectory;
    private readonly Func<string, CancellationToken, Task<string?>> readHooksPath;
    private readonly TimeProvider timeProvider;
    private readonly string? registrySubKey;

    /// <summary>
    /// <paramref name="registrySubKey"/> names the Apps &amp; features entry to look for.
    /// Tests point it at a key of their own so they never read or write the real one.
    /// </summary>
    public UninstallPlanner(
        InstallationLayout layout,
        string homeDirectory,
        Func<string, CancellationToken, Task<string?>>? readHooksPath = null,
        TimeProvider? timeProvider = null,
        string? registrySubKey = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        this.layout = layout;
        this.homeDirectory = homeDirectory;
        this.readHooksPath = readHooksPath ?? ReadHooksPathWithGit;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.registrySubKey = registrySubKey;
    }

    public async Task<UninstallPlan> PlanAsync(
        RemovalSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var hooks = await PlanHooksAsync(selection, cancellationToken);
        var (removed, retained) = PlanPaths(selection);
        return new UninstallPlan(
            layout.Root,
            selection,
            removed,
            retained,
            PlanAgentConfigurations(selection),
            hooks,
            Retained(selection, hooks),
            PlansAppsAndFeaturesRemoval(selection));
    }

    /// <summary>
    /// Whether there is an Apps &amp; features entry to take out. It goes with the binaries:
    /// an entry offering to uninstall an installation whose binaries are gone is an entry
    /// pointing at nothing, and one kept beside a runtime that stays is still true.
    /// </summary>
    private bool PlansAppsAndFeaturesRemoval(RemovalSelection selection) =>
        selection.Includes(RemovalItem.Binaries) &&
        OperatingSystem.IsWindows() &&
        new UninstallRegistration(layout, registrySubKey).Read() is not null;

    /// <summary>
    /// Every top-level entry of the runtime root, split into what this selection takes and
    /// what it leaves.
    ///
    /// Only the top level: a directory is removed whole, and listing what is inside it would
    /// turn a review page into a file manager without telling the reader anything the
    /// directory's own name does not already say. What is left is returned rather than
    /// inferred, so "does the root survive this" is answered by the disk instead of by the
    /// checkboxes.
    /// </summary>
    private (IReadOnlyList<RemovalPathEntry> Removed, IReadOnlyList<string> Retained) PlanPaths(
        RemovalSelection selection)
    {
        if (!Directory.Exists(layout.Root))
        {
            return ([], []);
        }

        var removed = new List<RemovalPathEntry>();
        var retained = new List<string>();
        foreach (var path in SafeEnumerate(layout.Root))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0)
            {
                continue;
            }

            var item = RemovalMatrix.ClassifyRootEntry(name);
            if (selection.Includes(item))
            {
                removed.Add(new RemovalPathEntry(item, path, Directory.Exists(path)));
            }
            else
            {
                retained.Add(path);
            }
        }

        return (
            removed
                .OrderBy(entry => entry.Item)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            retained.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private IReadOnlyList<AgentConfigurationPlan> PlanAgentConfigurations(
        RemovalSelection selection)
    {
        var plans = new List<AgentConfigurationPlan>();
        if (selection.Includes(RemovalItem.ClaudeIntegration))
        {
            plans.Add(new ClaudeConfigurationAdapter(
                homeDirectory,
                layout.LauncherDirectory,
                timeProvider).PreviewRemoval());
        }

        if (selection.Includes(RemovalItem.CodexIntegration))
        {
            plans.Add(new CodexConfigurationAdapter(
                homeDirectory,
                layout.LauncherDirectory,
                timeProvider).PreviewRemoval());
        }

        return plans;
    }

    private async Task<IReadOnlyList<HookRemovalEntry>> PlanHooksAsync(
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        if (!selection.Includes(RemovalItem.GitHooks))
        {
            return [];
        }

        var repositoriesRoot = Path.Combine(layout.Root, "repositories");
        if (!Directory.Exists(repositoriesRoot))
        {
            return [];
        }

        var entries = new List<HookRemovalEntry>();
        foreach (var repositoryRuntimeRoot in SafeEnumerate(repositoriesRoot)
                     .Where(Directory.Exists)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryId = Path.GetFileName(repositoryRuntimeRoot);
            if (!selection.IncludesRepository(repositoryId))
            {
                continue;
            }

            entries.Add(await PlanRepositoryHooksAsync(
                repositoryId,
                repositoryRuntimeRoot,
                cancellationToken));
        }

        return entries;
    }

    private async Task<HookRemovalEntry> PlanRepositoryHooksAsync(
        string repositoryId,
        string repositoryRuntimeRoot,
        CancellationToken cancellationToken)
    {
        RepositoryManifest? manifest;
        try
        {
            manifest = new RepositoryManifestStore(repositoryRuntimeRoot).Read();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Skipped(repositoryId, repositoryRuntimeRoot, "its manifest is unreadable");
        }

        if (manifest is null)
        {
            return Skipped(repositoryId, repositoryRuntimeRoot, "it has no manifest");
        }

        if (!Directory.Exists(manifest.CommonDirectory))
        {
            // Moved or deleted since it was connected. Naming it is the whole answer: there is
            // nothing to remove, and stopping the uninstall over it would be worse than useless.
            return Skipped(
                repositoryId,
                manifest.CommonDirectory,
                "the repository no longer exists at this path");
        }

        var workingTreeRoot = manifest.ActiveWorktrees
            .Select(worktree => worktree.Path)
            .FirstOrDefault(Directory.Exists);
        string? configuredHooksPath = null;
        try
        {
            configuredHooksPath = await readHooksPath(
                workingTreeRoot ?? manifest.CommonDirectory,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Git is a prerequisite, not a guarantee: it may have been uninstalled before
            // LocalAi was. Without it the answer is the default location, which is where the
            // dispatchers are in every repository that never set core.hooksPath.
        }

        var hooksDirectory = GitHookLayout.ResolveHooksDirectory(
            manifest.CommonDirectory,
            configuredHooksPath,
            workingTreeRoot);
        var dispatchers = new List<string>();
        var restored = new List<string>();
        foreach (var hookEvent in GitHookLayout.Events)
        {
            var hookPath = Path.Combine(hooksDirectory, hookEvent);
            if (!GitHookLayout.IsManagedDispatcher(hookPath))
            {
                continue;
            }

            dispatchers.Add(hookPath);
            var chained = hookPath + GitHookLayout.ChainedSuffix;
            if (File.Exists(chained))
            {
                restored.Add(chained);
            }
        }

        var (excludePath, excludePatterns) = PlanExclude(
            manifest.CommonDirectory,
            workingTreeRoot,
            hooksDirectory,
            dispatchers);
        return new HookRemovalEntry(
            repositoryId,
            manifest.CommonDirectory,
            hooksDirectory,
            dispatchers,
            restored,
            excludePatterns,
            excludePath,
            null);
    }

    /// <summary>
    /// The <c>.git/info/exclude</c> lines installation added for this repository, and only
    /// those that are actually in the file. The header goes with them: it was written to
    /// introduce them, and with the last of them gone it introduces nothing.
    /// </summary>
    private static (string? Path, IReadOnlyList<string> Patterns) PlanExclude(
        string commonDirectory,
        string? workingTreeRoot,
        string hooksDirectory,
        IReadOnlyList<string> dispatchers)
    {
        if (dispatchers.Count == 0 ||
            !GitHookLayout.IsInsideWorkingTree(hooksDirectory, commonDirectory, workingTreeRoot))
        {
            return (null, []);
        }

        var excludePath = Path.Combine(Path.GetFullPath(commonDirectory), "info", "exclude");
        if (!File.Exists(excludePath))
        {
            return (null, []);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(excludePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return (null, []);
        }

        var written = GitHookLayout
            .ExcludePatterns(workingTreeRoot!, dispatchers)
            .Where(pattern => lines.Contains(pattern, StringComparer.Ordinal))
            .ToList();
        if (written.Count == 0)
        {
            return (null, []);
        }

        if (lines.Contains(GitHookLayout.ExcludeHeader, StringComparer.Ordinal))
        {
            written.Insert(0, GitHookLayout.ExcludeHeader);
        }

        return (excludePath, written);
    }

    private static HookRemovalEntry Skipped(
        string repositoryId,
        string commonDirectory,
        string reason) =>
        new(repositoryId, commonDirectory, null, [], [], [], null, reason);

    /// <summary>
    /// What this run leaves alone, and how to deal with each one by hand. Two of them are
    /// machine-wide and belong to other software as much as to LocalAi; one is the record of
    /// what installs did and outlives them on purpose; the rest are whatever the matrix was
    /// told to keep.
    /// </summary>
    private IReadOnlyList<RetainedNotice> Retained(
        RemovalSelection selection,
        IReadOnlyList<HookRemovalEntry> hooks)
    {
        var notices = new List<RetainedNotice>
        {
            new(
                "Prerequisites installed through winget",
                "Git, Ollama, the .NET SDK, Node, Python and the indexers are machine-wide and " +
                "other software may use them. Remove the ones you no longer want with " +
                "`winget uninstall <id>`."),
            new(
                "Ollama models",
                "They may serve other tools. Remove the ones you no longer want with " +
                "`ollama rm <tag>`."),
            new(
                "The installer journal",
                "%LOCALAPPDATA%\\" + RemovalMatrix.JournalDirectoryName + " stays: it is the " +
                "record of what installs did to this machine, and this run writes its own " +
                "entry there too."),
        };

        if (!selection.Includes(RemovalItem.SigningKeys))
        {
            notices.Add(new(
                RemovalMatrix.Title(RemovalItem.SigningKeys),
                Path.Combine(layout.Root, RemovalMatrix.SigningKeyDirectoryName) +
                " stays. Removing it needs its own confirmation, because the offline backup " +
                "would then be the only copy."));
        }

        foreach (var item in new[]
                 {
                     RemovalItem.RepositoryIndexes,
                     RemovalItem.Settings,
                 })
        {
            if (!selection.Includes(item))
            {
                notices.Add(new(RemovalMatrix.Title(item), RemovalMatrix.Note(item)));
            }
        }

        // A launcher that is gone with dispatchers still pointing at it is the one combination
        // the matrix allows that leaves something visibly broken behind, so it is stated rather
        // than left to be discovered at the next commit.
        if (selection.Includes(RemovalItem.Binaries) &&
            !selection.Includes(RemovalItem.GitHooks))
        {
            notices.Add(new(
                "Git hook dispatchers",
                "They stay installed and will call a launcher that is no longer there. Each " +
                "hook exits non-zero from then on; remove them here or by hand."));
        }

        var skipped = hooks.Where(hook => hook.IsSkipped).ToArray();
        if (skipped.Length > 0)
        {
            notices.Add(new(
                "Repositories that could not be reached",
                string.Join(
                    "; ",
                    skipped.Select(hook => hook.CommonDirectory + " — " + hook.SkipReason))));
        }

        return notices;
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<string?> ReadHooksPathWithGit(
        string workingDirectory,
        CancellationToken cancellationToken) =>
        await new GitClient().GetConfigurationAsync(
            workingDirectory,
            "core.hooksPath",
            cancellationToken);
}
