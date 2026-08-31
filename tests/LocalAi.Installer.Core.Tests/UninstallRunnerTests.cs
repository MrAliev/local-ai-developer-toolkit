using LocalAi.TestFixtures;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Performing the plan: exactly what the preview named, in the one order that is safe, with
/// every effect journalled and every failure reported rather than swallowed.
/// </summary>
public sealed class UninstallRunnerTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    private readonly string journalDirectory = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.RemovalJournals",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A key of this test's own, so nothing here reads or removes the real Apps &amp; features
    /// entry of a machine that genuinely has LocalAi installed.
    /// </summary>
    private readonly string registrySubKey =
        @"Software\LocalAi.Tests\" + Guid.NewGuid().ToString("N") + @"\Uninstall\LocalAi";

    public void Dispose()
    {
        machine.Dispose();
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\LocalAi.Tests\" + registrySubKey.Split('\\')[2],
                throwOnMissingSubKey: false);
            Directory.Delete(journalDirectory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    [Fact]
    public async Task Apply_removes_exactly_what_the_preview_named()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));
        var named = plan.Paths.Select(entry => entry.Path).ToArray();

        var outcome = await Apply(plan);

        Assert.True(outcome.Succeeded);
        Assert.Equal(named, outcome.RemovedPaths);
        Assert.All(named, path => Assert.False(
            File.Exists(path) || Directory.Exists(path),
            path + " should be gone"));
        // The one thing a default full uninstall keeps, still whole.
        Assert.True(File.Exists(Path.Combine(
            machine.Runtime,
            RemovalMatrix.SigningKeyDirectoryName,
            "private.key")));
    }

    [Fact]
    public async Task A_reinstall_friendly_run_leaves_the_indexes_and_settings_byte_identical()
    {
        var repositories = Path.Combine(machine.Runtime, "repositories");
        var before = RemovalFixture.Snapshot(repositories);
        var settings = RemovalMatrix.SettingsFileNames
            .ToDictionary(
                name => name,
                name => File.ReadAllBytes(Path.Combine(machine.Runtime, name)));

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly)));

        Assert.True(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
        Assert.False(outcome.RuntimeRootRemoved);
        Assert.Equal(before, RemovalFixture.Snapshot(repositories));
        foreach (var (name, content) in settings)
        {
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(machine.Runtime, name)));
        }
    }

    [Fact]
    public async Task Disconnecting_clients_leaves_the_runtime_untouched()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var runner = new RecordingProcessRunner();

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)),
            runner);

        Assert.True(outcome.Succeeded);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        // Nothing of the runtime is going, so interrupting a broker that may be mid-inference
        // would be a gratuitous stop.
        Assert.Empty(runner.Calls);
        Assert.False(outcome.ProcessesStopped);
    }

    /// <summary>
    /// The order the whole run depends on. Deleting the files underneath a running broker
    /// leaves a half-removed tree and a process still holding what is left of it, so the stop
    /// is observed to happen while everything is still there — a condition, not a delay.
    /// </summary>
    [Fact]
    public async Task The_broker_is_asked_to_finish_before_the_root_is_touched()
    {
        var runtimeIntactAtStop = false;
        var runner = new RecordingProcessRunner(() =>
            runtimeIntactAtStop = Directory.Exists(Path.Combine(machine.Runtime, "bin")) &&
                Directory.Exists(Path.Combine(machine.Runtime, "jobs")));

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)),
            runner);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(machine.LauncherPath, call.Executable);
        Assert.Equal(["stop"], call.Arguments);
        Assert.True(runtimeIntactAtStop, "the stop must come before anything is removed");
        Assert.True(outcome.ProcessesStopped);
    }

    [Fact]
    public async Task A_broker_that_will_not_stop_refuses_the_run_before_anything_is_removed()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessResult(1, string.Empty, "broker_still_running", false, false),
        };
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var refusal = await Assert.ThrowsAsync<UninstallRefusedException>(() =>
            Runner(runner).ApplyAsync(plan, journal, TestContext.Current.CancellationToken));

        Assert.Contains("nothing was removed", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("broker_still_running", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        var step = Assert.Single(journal.Snapshot.Steps);
        Assert.Equal(InstallerRunEffectKind.ProcessStop, step.Kind);
        Assert.Equal(InstallerRunStepStatus.Failed, step.Status);
    }

    /// <summary>
    /// A broken installation is exactly the one somebody most needs to clear up, so a missing
    /// launcher is not a reason to refuse — there is nothing left to ask.
    /// </summary>
    [Fact]
    public async Task A_missing_launcher_does_not_block_the_removal()
    {
        File.Delete(machine.LauncherPath);
        var runner = new RecordingProcessRunner();

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)),
            runner);

        Assert.Empty(runner.Calls);
        Assert.False(outcome.ProcessesStopped);
        Assert.True(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
    }

    [Fact]
    public async Task The_runtime_root_itself_goes_once_the_run_has_emptied_it()
    {
        var outcome = await Apply(await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .WithSigningKeyRemoval(true)));

        Assert.True(outcome.RuntimeRootRemoved);
        Assert.False(Directory.Exists(machine.Runtime));
        // The installer's record of what it did lives outside the root and outlives it.
        Assert.True(Directory.Exists(journalDirectory));
    }

    [Fact]
    public async Task Client_files_are_rewritten_on_disk_with_the_users_text_intact()
    {
        var claudeInstructions = Path.Combine(machine.Home, ".claude", "CLAUDE.md");
        var codexConfiguration = Path.Combine(machine.Home, ".codex", "config.toml");

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)));

        Assert.True(outcome.Succeeded);
        Assert.Equal(
            RemovalFixture.ClaudeInstructionsPreamble,
            File.ReadAllText(claudeInstructions));
        var codex = File.ReadAllText(codexConfiguration);
        Assert.DoesNotContain("mcp_servers.codesearch", codex, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_servers.locallm", codex, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]", codex, StringComparison.Ordinal);
        Assert.Contains(claudeInstructions, outcome.RewrittenConfigurations);
    }

    [Fact]
    public async Task Removing_a_dispatcher_puts_back_the_hook_it_was_chaining()
    {
        var chained = Path.Combine(machine.PlainHooks, "post-commit");
        var foreign = Path.Combine(machine.PlainHooks, "post-checkout");

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)
                .With(RemovalItem.GitHooks, true)));

        Assert.True(outcome.Succeeded);
        Assert.Equal(RemovalFixture.ChainedHookBody, File.ReadAllText(chained));
        Assert.False(File.Exists(chained + ".pre-localai"));
        Assert.False(File.Exists(Path.Combine(machine.PlainHooks, "post-merge")));
        Assert.Equal(RemovalFixture.ForeignHookBody, File.ReadAllText(foreign));
    }

    [Fact]
    public async Task The_exclude_file_loses_our_lines_and_keeps_everybody_elses()
    {
        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)));

        Assert.True(outcome.Succeeded);
        Assert.Equal(
            "# git ls-files --others --exclude-from=.git/info/exclude\n" +
            "node_modules/\n",
            File.ReadAllText(machine.HuskyExclude));
        Assert.False(File.Exists(Path.Combine(machine.HuskyHooks, "post-commit")));
        // husky's own runner directory is not ours and stays exactly as it is.
        Assert.True(File.Exists(Path.Combine(machine.HuskyHooks, "_", "h")));
    }

    [Fact]
    public async Task A_repository_that_no_longer_exists_costs_the_run_nothing()
    {
        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)));

        Assert.True(outcome.Succeeded);
        Assert.DoesNotContain(
            outcome.RemovedHooks,
            path => path.Contains("deleted-repository", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One path another program is holding open stops that path, not the uninstall. The
    /// alternative — abandoning the run at the first locked file — leaves more behind than it
    /// removes and tells the person less about it.
    /// </summary>
    [Fact]
    public async Task A_path_something_else_is_holding_is_reported_rather_than_hidden()
    {
        var held = Path.Combine(machine.Runtime, "jobs", "job.json");
        using var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)));

        Assert.False(outcome.Succeeded);
        var failure = Assert.Single(outcome.Failures);
        Assert.Equal(Path.Combine(machine.Runtime, "jobs"), failure.Path);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
        Assert.False(outcome.RuntimeRootRemoved);
    }

    /// <summary>
    /// The journal answers "what is there now" for a machine an uninstall half-changed, so the
    /// intent is on disk before each effect runs. None of it is offered back: an uninstall is
    /// not a transaction, and a rollback offering to restore a tree it no longer has would be
    /// a promise nothing can keep.
    /// </summary>
    [Fact]
    public async Task Every_effect_is_journalled_intent_first_and_none_is_offered_back()
    {
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        await Runner(new RecordingProcessRunner()).ApplyAsync(
            plan,
            journal,
            TestContext.Current.CancellationToken);

        var steps = journal.Snapshot.Steps;
        Assert.Equal(InstallerRunEffectKind.ProcessStop, steps[0].Kind);
        Assert.Contains(steps, step => step.Kind == InstallerRunEffectKind.AgentConfiguration);
        Assert.Contains(steps, step => step.Kind == InstallerRunEffectKind.GitHookRemoval);
        Assert.Contains(steps, step => step.Kind == InstallerRunEffectKind.RuntimeRemoval);
        Assert.All(steps, step =>
        {
            Assert.Equal(InstallerRunStepStatus.Completed, step.Status);
            Assert.False(step.IsReversible);
        });
        Assert.False(journal.Snapshot.HasReversibleWork);
        foreach (var entry in plan.Paths)
        {
            Assert.Contains(
                steps,
                step => step.Description.Contains(entry.Path, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Removal has to take the Apps &amp; features entry with it: one that outlives the
    /// installation offers to uninstall something that is not there.
    /// </summary>
    [Fact]
    public async Task The_apps_and_features_entry_goes_with_the_binaries()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey, _ => { });
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);
        Assert.True(plan.RemovesAppsAndFeaturesEntry);
        Assert.Contains("Apps & features entry", plan.PreviewText, StringComparison.Ordinal);

        var outcome = await Apply(plan);

        Assert.True(outcome.AppsAndFeaturesEntryRemoved);
        Assert.False(outcome.UninstallerRemovalDeferred);
        Assert.Null(registration.Read());
        Assert.False(Directory.Exists(registration.UninstallerDirectory));
    }

    [Fact]
    public async Task Disconnecting_clients_leaves_the_entry_where_it_is()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey, _ => { });
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);

        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.DisconnectClients),
            registrySubKey);
        await Apply(plan);

        Assert.False(plan.RemovesAppsAndFeaturesEntry);
        Assert.NotNull(registration.Read());
    }

    /// <summary>
    /// The ordinary uninstall: Apps &amp; features started the copy inside the tree being
    /// deleted. Sweeping the runtime root must step around that one directory instead of
    /// reporting a failure over the file it is running from, and remove it at the very end.
    /// </summary>
    [Fact]
    public async Task The_directory_the_uninstaller_runs_from_is_swept_around_and_taken_last()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey, _ => { });
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);

        var outcome = await Apply(plan, selfDirectory: registration.UninstallerDirectory);

        Assert.True(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
        Assert.False(Directory.Exists(registration.UninstallerDirectory));
    }

    [Fact]
    public async Task A_copy_that_cannot_go_yet_is_reported_rather_than_failed()
    {
        var deferred = new List<string>();
        var registration = new UninstallRegistration(machine.Layout, registrySubKey, _ => { });
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);
        using var running = new FileStream(
            registration.UninstallerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var outcome = await Apply(
            plan,
            selfDirectory: registration.UninstallerDirectory,
            removeUninstallerAfterExit: deferred.Add);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.UninstallerRemovalDeferred);
        Assert.Equal([registration.UninstallerDirectory], deferred);
        Assert.Null(registration.Read());
    }

    private Task<UninstallPlan> Plan(
        RemovalSelection selection,
        string? registrySubKey = null) =>
        machine.PlanAsync(selection, TestContext.Current.CancellationToken, registrySubKey);

    private async Task<UninstallOutcome> Apply(
        UninstallPlan plan,
        RecordingProcessRunner? runner = null,
        string? selfDirectory = null,
        Action<string>? removeUninstallerAfterExit = null)
    {
        using var journal = InstallerRunJournal.Start(journalDirectory);
        return await Runner(
                runner ?? new RecordingProcessRunner(),
                selfDirectory,
                removeUninstallerAfterExit)
            .ApplyAsync(plan, journal, TestContext.Current.CancellationToken);
    }

    private UninstallRunner Runner(
        IProcessRunner runner,
        string? selfDirectory = null,
        Action<string>? removeUninstallerAfterExit = null) =>
        new(
            machine.Layout,
            runner,
            registrySubKey: registrySubKey,
            selfDirectory: selfDirectory,
            removeUninstallerAfterExit: removeUninstallerAfterExit ?? (_ => { }));

    /// <summary>
    /// Stands in for the launcher. <paramref name="onCall"/> runs at the moment the stop is
    /// requested, which is how the ordering these tests care about is observed rather than
    /// assumed.
    /// </summary>
    private sealed class RecordingProcessRunner(Action? onCall = null) : IProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public ProcessResult Result { get; set; } =
            new(0, string.Empty, string.Empty, false, false);

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            onCall?.Invoke();
            Calls.Add((executable, arguments));
            return Task.FromResult(Result);
        }
    }
}
