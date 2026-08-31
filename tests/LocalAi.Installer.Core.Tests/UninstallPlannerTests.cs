using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Removal;
using LocalAi.Repository;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The plan over a populated machine: what each preset would take, said before anything is
/// taken.
///
/// Everything here is a real directory tree rather than a mock file system, because the
/// mistakes this planner can make are all about what is actually on disk — an unrecognised
/// file nobody classified, a repository that moved, a hooks directory somewhere other than
/// $GIT_DIR/hooks — and none of those survive being described by a fake.
/// </summary>
public sealed class UninstallPlannerTests : IDisposable
{
    private const string ManagedDispatcher =
        "#!/bin/sh\n# LocalAi managed dispatcher\nlocalai-launcher.exe hook post-commit\n";

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.RemovalTests",
        Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string?> hooksPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private string LocalAppData => Path.Combine(root, "appdata");

    private string Runtime => Path.Combine(LocalAppData, "LocalAi");

    private string Home => Path.Combine(root, "home");

    public UninstallPlannerTests()
    {
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(Home);
        PopulateRuntime();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_full_uninstall_names_every_removal()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        Assert.True(plan.HasWork);
        foreach (var name in Directory.EnumerateFileSystemEntries(Runtime)
                     .Select(Path.GetFileName)
                     .Where(name => name != RemovalMatrix.SigningKeyDirectoryName))
        {
            var path = Path.Combine(Runtime, name!);
            Assert.Contains(plan.Paths, entry => entry.Path == path);
            Assert.Contains(path, plan.PreviewText, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The default full uninstall keeps the signing keys, so the root outlives it — and the
    /// plan says so from what is on disk rather than from which boxes were ticked. Confirming
    /// the keys is what empties it.
    /// </summary>
    [Fact]
    public async Task Whether_the_root_survives_is_read_from_what_is_left_in_it()
    {
        var keys = Path.Combine(Runtime, RemovalMatrix.SigningKeyDirectoryName);

        var withKeys = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));
        var withoutKeys = await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .WithSigningKeyRemoval(true));

        Assert.False(withKeys.RemovesRuntimeRootEntirely);
        Assert.Equal([keys], withKeys.RetainedPaths);
        Assert.Contains("keep " + keys, withKeys.PreviewText, StringComparison.Ordinal);
        Assert.True(withoutKeys.RemovesRuntimeRootEntirely);
        Assert.Empty(withoutKeys.RetainedPaths);
    }

    /// <summary>
    /// The loudest line on the page. A key directory removed by a preset, rather than by the
    /// person saying so a second time, is an offline backup silently promoted to sole copy.
    /// </summary>
    [Fact]
    public async Task The_signing_keys_stay_unless_they_are_separately_confirmed()
    {
        var keys = Path.Combine(Runtime, RemovalMatrix.SigningKeyDirectoryName);

        var kept = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));
        var removed = await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .WithSigningKeyRemoval(true));

        Assert.DoesNotContain(kept.Paths, entry => entry.Path == keys);
        Assert.Contains(
            kept.Retained,
            notice => notice.Detail.Contains(keys, StringComparison.Ordinal));
        Assert.Contains(removed.Paths, entry => entry.Path == keys);
    }

    [Fact]
    public async Task A_reinstall_friendly_run_leaves_the_indexes_and_settings_alone()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly));

        var removed = plan.Paths.Select(entry => entry.Path).ToArray();
        Assert.Contains(Path.Combine(Runtime, "bin"), removed);
        Assert.Contains(Path.Combine(Runtime, "jobs"), removed);
        Assert.DoesNotContain(Path.Combine(Runtime, "repositories"), removed);
        Assert.False(plan.RemovesRuntimeRootEntirely);
        Assert.All(
            RemovalMatrix.SettingsFileNames,
            name => Assert.DoesNotContain(Path.Combine(Runtime, name), removed));
        Assert.All(
            RemovalMatrix.SettingsFileNames,
            name => Assert.True(File.Exists(Path.Combine(Runtime, name))));
    }

    [Fact]
    public async Task Disconnecting_clients_touches_no_runtime_path()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        Assert.Empty(plan.Paths);
        Assert.True(plan.HasWork);
        Assert.Equal(["Claude", "Codex"], plan.AgentConfigurations.Select(agent => agent.AgentName));
    }

    /// <summary>
    /// The runtime root is shared, and a later release will put something here this installer
    /// has never heard of. A full uninstall that quietly stepped around it would leave the
    /// machine dirty while reporting success.
    /// </summary>
    [Fact]
    public async Task Unrecognised_runtime_files_are_listed_rather_than_left_behind()
    {
        var stranger = Path.Combine(Runtime, "something-a-later-release-added.json");

        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var entry = Assert.Single(plan.Paths, path => path.Path == stranger);
        Assert.Equal(RemovalItem.OtherRuntimeFiles, entry.Item);
        Assert.Contains(stranger, plan.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_selected_plans_nothing()
    {
        var plan = await Plan(RemovalSelection.Nothing());

        Assert.False(plan.HasWork);
        Assert.Empty(plan.Paths);
        Assert.Empty(plan.Hooks);
        Assert.Contains("would change nothing", plan.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_clients_lose_their_registrations_and_their_managed_block()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        var claudeJson = PlannedFile(plan, "Claude", ".claude.json");
        var claudeInstructions = PlannedFile(plan, "Claude", "CLAUDE.md");
        var codexToml = PlannedFile(plan, "Codex", "config.toml");
        var codexInstructions = PlannedFile(plan, "Codex", "AGENTS.md");

        Assert.DoesNotContain("codesearch", claudeJson.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain("locallm", claudeJson.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_servers.codesearch", codexToml.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_servers.locallm", codexToml.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ManagedInstructionBlock.BeginMarker,
            claudeInstructions.AfterText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ManagedInstructionBlock.BeginMarker,
            codexInstructions.AfterText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The promise the managed markers exist to make, checked from the other direction: an
    /// uninstall gives the file back exactly as the person had it.
    /// </summary>
    [Fact]
    public async Task What_the_user_wrote_outside_the_markers_survives_byte_for_byte()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        Assert.Equal(
            "# My own notes\r\n\r\nKeep this paragraph.\r\n",
            PlannedFile(plan, "Claude", "CLAUDE.md").AfterText);
        Assert.Equal(
            "# Codex house rules\n\nAlways run the tests.\n",
            PlannedFile(plan, "Codex", "AGENTS.md").AfterText);
    }

    [Fact]
    public async Task Another_clients_registrations_and_settings_are_not_ours_to_remove()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        var claudeJson = PlannedFile(plan, "Claude", ".claude.json").AfterText;
        var codexToml = PlannedFile(plan, "Codex", "config.toml").AfterText;

        Assert.Contains("\"someone-elses-server\"", claudeJson, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"dark\"", claudeJson, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]", codexToml, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-5\"", codexToml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hooks_are_listed_from_the_manifests_with_their_chained_originals()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "plain");
        var hooksDirectory = Path.Combine(root, "plain", ".git", "hooks");
        Assert.Equal(hooksDirectory, hook.HooksDirectory);
        Assert.Equal(
            [Path.Combine(hooksDirectory, "post-commit"), Path.Combine(hooksDirectory, "post-merge")],
            hook.Dispatchers);
        Assert.Equal(
            [Path.Combine(hooksDirectory, "post-commit.pre-localai")],
            hook.RestoredHooks);
        Assert.Empty(hook.ExcludePatterns);
    }

    /// <summary>
    /// A hook the person wrote themselves carries no marker. Installation chains it rather than
    /// replacing it, and removal has to leave it exactly where it is.
    /// </summary>
    [Fact]
    public async Task A_hook_that_is_not_ours_is_left_alone()
    {
        var foreign = Path.Combine(root, "plain", ".git", "hooks", "post-checkout");

        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "plain");
        Assert.DoesNotContain(foreign, hook.Dispatchers);
        Assert.DoesNotContain(foreign, plan.PreviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// husky points core.hooksPath at a directory it rewrites on every npm install, so the
    /// dispatchers live in its parent. Removal has to look where installation actually wrote
    /// them, or it reports success over hooks that are still there.
    /// </summary>
    [Fact]
    public async Task Hooks_are_found_where_core_hooksPath_sends_them()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "husky");
        Assert.Equal(Path.Combine(root, "husky", ".husky"), hook.HooksDirectory);
        Assert.Equal(
            [Path.Combine(root, "husky", ".husky", "post-commit")],
            hook.Dispatchers);
    }

    /// <summary>
    /// A hooks directory inside the working tree made installation write ignore rules into
    /// .git/info/exclude — one for the dispatcher and one for the chained original it parks
    /// beside it. Both were ours to write and both are ours to take back, and the header goes
    /// with them because it introduces nothing once they are gone.
    /// </summary>
    [Fact]
    public async Task The_exclude_lines_installation_wrote_are_named()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "husky");
        Assert.Equal(
            Path.Combine(root, "husky", ".git", "info", "exclude"),
            hook.ExcludePath);
        Assert.Equal(
            [
                "# LocalAi managed Git hooks",
                "/.husky/post-commit",
                "/.husky/post-commit.pre-localai",
            ],
            hook.ExcludePatterns);
        Assert.Contains("/.husky/post-commit", plan.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_that_no_longer_exists_is_skipped_and_named()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "gone");
        Assert.True(hook.IsSkipped);
        Assert.False(hook.HasWork);
        Assert.Contains("no longer exists", hook.SkipReason!, StringComparison.Ordinal);
        Assert.Contains(hook.CommonDirectory, plan.PreviewText, StringComparison.Ordinal);
        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains(hook.CommonDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hook_removal_can_be_narrowed_to_chosen_repositories()
    {
        var plan = await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .WithRepositories(["husky"]));

        Assert.Equal(["husky"], plan.Hooks.Select(hook => hook.RepositoryId));
    }

    /// <summary>
    /// The one combination the matrix allows that leaves something visibly broken: hooks that
    /// call a launcher which is no longer installed. Saying so is cheap; finding out at the
    /// next commit is not.
    /// </summary>
    [Fact]
    public async Task Keeping_the_hooks_while_removing_the_binaries_is_called_out()
    {
        var plan = await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .With(RemovalItem.GitHooks, false));

        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains(
                "launcher that is no longer there",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_is_never_touched_is_stated_on_every_run()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains("winget uninstall", StringComparison.Ordinal));
        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains("ollama rm", StringComparison.Ordinal));
        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains(
                RemovalMatrix.JournalDirectoryName,
                StringComparison.Ordinal));
    }

    private async Task<UninstallPlan> Plan(RemovalSelection selection) =>
        await new UninstallPlanner(
                InstallationLayout.FromLocalAppData(LocalAppData),
                Home,
                (workingDirectory, _) => Task.FromResult(
                    hooksPaths.GetValueOrDefault(Path.GetFullPath(workingDirectory))))
            .PlanAsync(selection, TestContext.Current.CancellationToken);

    private static AgentConfigurationFilePlan PlannedFile(
        UninstallPlan plan,
        string agent,
        string suffix) =>
        plan.AgentConfigurations
            .Single(configuration => configuration.AgentName == agent)
            .Files
            .Single(file => file.Path.EndsWith(suffix, StringComparison.Ordinal));

    private void PopulateRuntime()
    {
        WriteFile(Path.Combine(Runtime, "bin", "current.json"), "{\"schemaVersion\":1,\"version\":\"0.1.50\"}");
        WriteFile(Path.Combine(Runtime, "bin", "versions", "0.1.50", "localai.exe"), "binary");
        WriteFile(Path.Combine(Runtime, "bin", "launcher", "localai-launcher.exe"), "binary");
        WriteFile(Path.Combine(Runtime, "installer", "backups", "launcher-1", "localai-launcher.exe"), "binary");
        foreach (var name in RemovalMatrix.SettingsFileNames)
        {
            WriteFile(Path.Combine(Runtime, name), "{\"schemaVersion\":1}");
        }

        WriteFile(Path.Combine(Runtime, "jobs", "job.json"), "{}");
        WriteFile(Path.Combine(Runtime, "telemetry", "metrics", "record.json"), "{}");
        WriteFile(Path.Combine(Runtime, "host.json"), "{}");
        WriteFile(Path.Combine(Runtime, RemovalMatrix.SigningKeyDirectoryName, "private.key"), "secret");
        WriteFile(Path.Combine(Runtime, "something-a-later-release-added.json"), "{}");

        PopulatePlainRepository();
        PopulateHuskyRepository();
        WriteManifest("gone", Path.Combine(root, "deleted-repository", ".git"), []);

        PopulateClaude();
        PopulateCodex();
    }

    /// <summary>A repository with the dispatchers where Git looks by default.</summary>
    private void PopulatePlainRepository()
    {
        var repository = Path.Combine(root, "plain");
        var hooks = Path.Combine(repository, ".git", "hooks");
        WriteFile(Path.Combine(hooks, "post-commit"), ManagedDispatcher);
        WriteFile(Path.Combine(hooks, "post-commit.pre-localai"), "#!/bin/sh\necho mine\n");
        WriteFile(Path.Combine(hooks, "post-merge"), ManagedDispatcher);
        WriteFile(Path.Combine(hooks, "post-checkout"), "#!/bin/sh\necho entirely mine\n");
        WriteManifest("plain", Path.Combine(repository, ".git"), [repository]);
    }

    /// <summary>
    /// A husky repository: core.hooksPath points at .husky/_, the dispatchers are in .husky,
    /// and .git/info/exclude carries the ignore rules installation added for them.
    /// </summary>
    private void PopulateHuskyRepository()
    {
        var repository = Path.Combine(root, "husky");
        WriteFile(Path.Combine(repository, ".husky", "_", "h"), "husky runner\n");
        WriteFile(Path.Combine(repository, ".husky", "post-commit"), ManagedDispatcher);
        WriteFile(
            Path.Combine(repository, ".git", "info", "exclude"),
            "# git ls-files --others --exclude-from=.git/info/exclude\n" +
            "\n" +
            "# LocalAi managed Git hooks\n" +
            "/.husky/post-commit\n" +
            "/.husky/post-commit.pre-localai\n");
        hooksPaths[Path.GetFullPath(repository)] = ".husky/_";
        WriteManifest("husky", Path.Combine(repository, ".git"), [repository]);
    }

    private void PopulateClaude()
    {
        WriteFile(
            Path.Combine(Home, ".claude.json"),
            """
            {
              "theme": "dark",
              "mcpServers": {
                "someone-elses-server": {"command": "other.exe", "args": []},
                "codesearch": {"command": "localai-launcher.exe", "args": ["run", "codesearch-mcp"]},
                "locallm": {"command": "localai-launcher.exe", "args": ["run", "locallm-mcp"]}
              }
            }
            """);
        WriteFile(
            Path.Combine(Home, ".claude", "CLAUDE.md"),
            "# My own notes\r\n\r\nKeep this paragraph.\r\n" +
            ManagedInstructionBlock.Block + "\r\n");
    }

    private void PopulateCodex()
    {
        WriteFile(
            Path.Combine(Home, ".codex", "config.toml"),
            "model = \"gpt-5\"\n\n" +
            "[mcp_servers.other]\n" +
            "command = \"other.exe\"\n" +
            "args = []\n\n" +
            "[mcp_servers.codesearch]\n" +
            "command = \"localai-launcher.exe\"\n" +
            "args = [\"run\", \"codesearch-mcp\"]\n" +
            "default_tools_approval_mode = \"prompt\"\n\n" +
            "[mcp_servers.codesearch.tools.search_code]\n" +
            "approval_mode = \"approve\"\n\n" +
            "[mcp_servers.locallm]\n" +
            "command = \"localai-launcher.exe\"\n" +
            "args = [\"run\", \"locallm-mcp\"]\n" +
            "default_tools_approval_mode = \"prompt\"\n");
        WriteFile(
            Path.Combine(Home, ".codex", "AGENTS.md"),
            "# Codex house rules\n\nAlways run the tests.\n" +
            ManagedInstructionBlock.Block + "\n");
    }

    private void WriteManifest(
        string repositoryId,
        string commonDirectory,
        IReadOnlyList<string> worktrees) =>
        new RepositoryManifestStore(Path.Combine(Runtime, "repositories", repositoryId)).Save(
            new RepositoryManifest(
                repositoryId,
                commonDirectory,
                "main",
                "generation-1",
                "tree-1",
                "qwen3-embedding:8b-q8_0",
                4096,
                1,
                1,
                RepositoryIndexState.Current,
                worktrees
                    .Select(path => new RepositoryWorktree(path, "head", "main"))
                    .ToArray(),
                new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)));

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(
            path,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
