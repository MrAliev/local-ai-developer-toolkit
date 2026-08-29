using System.Runtime.Versioning;
using System.Security.Cryptography;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class InstallerRunRollbackTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.RunRollback.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly string journalDirectory;
    private readonly InstallationLayout layout;

    public InstallerRunRollbackTests()
    {
        journalDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(root);
        layout = InstallationLayout.FromLocalAppData(Path.Combine(root, "appdata"));
    }

    [Fact]
    public async Task Reactivates_the_prior_version_through_the_guarded_launcher_swap()
    {
        WritePointer("v2");
        WriteLauncher();
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var step = journal.BeginStep(
            InstallerRunEffectKind.PackageActivation,
            "LocalAi package v2");
        journal.CompleteStep(
            step,
            "Activated v2; v1 was active before.",
            isReversible: true,
            new InstallerRunUndoData("v2", "v1"));
        journal.Finish(InstallerRunOutcome.Failed);
        var expectedGuard = Convert.ToHexString(
            SHA256.HashData(CurrentPointerSnapshot.CreateCanonicalBytes("v2")));
        var runner = new RecordingRunner((_, arguments, _, _) =>
        {
            WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        });

        var result = await Rollback(runner).RollbackAsync(journal, TestContext.Current.CancellationToken);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(layout.LauncherPath, call.Executable);
        Assert.Equal(
            ["activate", "v1", "--stop-running", "--if-current-sha256", expectedGuard],
            call.Arguments);
        var report = Assert.Single(result.Steps);
        Assert.Equal(InstallerRollbackStepOutcome.Undone, report.Outcome);
        Assert.True(result.AllReversibleUndone);
        Assert.Equal(
            InstallerRunOutcome.RolledBack,
            InstallerRunJournal.Load(journal.JournalPath).Snapshot.Outcome);
    }

    /// <summary>
    /// A pointer that no longer says what this run wrote belongs to somebody else's
    /// activation. Overwriting it would make rollback the thing that breaks the machine.
    /// </summary>
    [Fact]
    public async Task Leaves_the_pointer_alone_when_another_version_became_active()
    {
        WritePointer("v3");
        WriteLauncher();
        using var journal = JournalWithActivation("v2", "v1");
        var runner = new RecordingRunner((_, _, _, _) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false)));

        var result = await Rollback(runner).RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Empty(runner.Calls);
        var report = Assert.Single(result.Steps);
        Assert.Equal(InstallerRollbackStepOutcome.Skipped, report.Outcome);
        Assert.Equal(
            CurrentPointerSnapshot.CreateCanonicalBytes("v3"),
            File.ReadAllBytes(layout.CurrentPointerPath));
    }

    [Fact]
    public async Task A_refused_reactivation_is_reported_as_a_failure()
    {
        WritePointer("v2");
        WriteLauncher();
        using var journal = JournalWithActivation("v2", "v1");
        var runner = new RecordingRunner((_, _, _, _) =>
            Task.FromResult(new ProcessResult(7, "", "", false, false)));

        var result = await Rollback(runner).RollbackAsync(journal, TestContext.Current.CancellationToken);

        var report = Assert.Single(result.Steps);
        Assert.Equal(InstallerRollbackStepOutcome.Failed, report.Outcome);
        Assert.False(result.AllReversibleUndone);
        Assert.Equal(
            InstallerRunOutcome.RollbackIncomplete,
            InstallerRunJournal.Load(journal.JournalPath).Snapshot.Outcome);
    }

    [Fact]
    public async Task Removes_a_file_the_run_created_when_it_is_still_what_the_run_wrote()
    {
        var path = Path.Combine(root, "runtime", "ollama-launch.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var written = "{\"path\":\"C:/ollama.exe\"}"u8.ToArray();
        File.WriteAllBytes(path, written);
        using var journal = JournalWithFileStep(CreatedFileUndo(path, written));

        var result = await Rollback(NoProcessRunner()).RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal(InstallerRollbackStepOutcome.Undone, Assert.Single(result.Steps).Outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Restores_a_replaced_file_from_the_inline_pre_install_content()
    {
        var path = Path.Combine(root, "runtime", "policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = "{\"residency\":\"RequireFullVram\"}"u8.ToArray();
        var after = "{\"residency\":\"AllowCpu\"}"u8.ToArray();
        File.WriteAllBytes(path, after);
        using var journal = JournalWithFileStep(new InstallerRunFileUndo(
            path,
            true,
            Convert.ToHexString(SHA256.HashData(before)),
            Convert.ToBase64String(before),
            null,
            Convert.ToHexString(SHA256.HashData(after))));

        var result = await Rollback(NoProcessRunner()).RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal(InstallerRollbackStepOutcome.Undone, Assert.Single(result.Steps).Outcome);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A file the user edited after the run is theirs now. Restoring the pre-install copy
    /// over their edit would destroy work under the banner of undoing the installer's.
    /// </summary>
    [Fact]
    public async Task Leaves_a_file_alone_when_it_changed_after_the_run()
    {
        var path = Path.Combine(root, "runtime", "policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var written = "{\"residency\":\"AllowCpu\"}"u8.ToArray();
        var edited = "{\"residency\":\"AllowCpu\",\"edited\":true}"u8.ToArray();
        File.WriteAllBytes(path, edited);
        using var journal = JournalWithFileStep(CreatedFileUndo(path, written));

        var result = await Rollback(NoProcessRunner()).RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal(InstallerRollbackStepOutcome.Skipped, Assert.Single(result.Steps).Outcome);
        Assert.Equal(edited, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Restores_an_agent_configuration_from_its_backup_file()
    {
        var path = Path.Combine(root, "home", ".claude.json");
        var backupPath = path + ".20260829-000000.bak";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = "{\"mcpServers\":{}}"u8.ToArray();
        var after = "{\"mcpServers\":{\"codesearch\":{}}}"u8.ToArray();
        File.WriteAllBytes(path, after);
        File.WriteAllBytes(backupPath, before);
        using var journal = JournalWithFileStep(new InstallerRunFileUndo(
            path,
            true,
            Convert.ToHexString(SHA256.HashData(before)),
            null,
            backupPath,
            Convert.ToHexString(SHA256.HashData(after))));

        var result = await Rollback(NoProcessRunner()).RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal(InstallerRollbackStepOutcome.Undone, Assert.Single(result.Steps).Outcome);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A restore that cannot prove it holds the pre-install bytes is a mutation, not a
    /// rollback. A backup that has rotted away — or was tampered with — fails the step
    /// instead of writing whatever is left of it.
    /// </summary>
    [Fact]
    public async Task Refuses_to_restore_from_a_backup_that_does_not_match_its_hash()
    {
        var path = Path.Combine(root, "home", ".claude.json");
        var backupPath = path + ".20260829-000000.bak";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var before = "{\"mcpServers\":{}}"u8.ToArray();
        var after = "{\"mcpServers\":{\"codesearch\":{}}}"u8.ToArray();
        File.WriteAllBytes(path, after);
        File.WriteAllBytes(backupPath, "{\"tampered\":true}"u8.ToArray());
        using var journal = JournalWithFileStep(new InstallerRunFileUndo(
            path,
            true,
            Convert.ToHexString(SHA256.HashData(before)),
            null,
            backupPath,
            Convert.ToHexString(SHA256.HashData(after))));

        var result = await Rollback(NoProcessRunner()).RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal(InstallerRollbackStepOutcome.Failed, Assert.Single(result.Steps).Outcome);
        Assert.False(result.AllReversibleUndone);
        Assert.Equal(after, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Irreversible_effects_are_reported_left_in_place_and_untouched()
    {
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var step = journal.BeginStep(
            InstallerRunEffectKind.DependencyInstall,
            "Prerequisite Git (Git.Git)");
        journal.CompleteStep(step, "Installed machine-wide.", isReversible: false);
        journal.Finish(InstallerRunOutcome.Failed);
        var runner = NoProcessRunner();

        var result = await Rollback(runner).RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Empty(runner.Calls);
        var report = Assert.Single(result.Steps);
        Assert.Equal(InstallerRollbackStepOutcome.LeftInPlace, report.Outcome);
        Assert.Contains("Not undone", report.Detail);
        Assert.True(result.AllReversibleUndone);
    }

    /// <summary>
    /// A step whose outcome was never written is the trace of a killed process: the effect
    /// may or may not have happened, and there is no undo data either way. Saying "state
    /// unknown, check by hand" is the only honest report.
    /// </summary>
    [Fact]
    public async Task A_step_that_never_finished_is_reported_as_unknown_state()
    {
        using var journal = InstallerRunJournal.Start(journalDirectory);
        journal.BeginStep(
            InstallerRunEffectKind.AgentConfiguration,
            "Claude client configuration");

        var result = await Rollback(NoProcessRunner()).RollbackAsync(journal, TestContext.Current.CancellationToken);

        var report = Assert.Single(result.Steps);
        Assert.Equal(InstallerRollbackStepOutcome.LeftInPlace, report.Outcome);
        Assert.Contains("unknown", report.Detail);
    }

    /// <summary>
    /// Effects are undone newest first, the way nested changes have to come apart: the agent
    /// registration that points at a launcher goes before the activation that installed it.
    /// </summary>
    [Fact]
    public async Task Undoes_effects_in_reverse_order_of_application()
    {
        var first = Path.Combine(root, "runtime", "policy.json");
        var second = Path.Combine(root, "runtime", "ollama-launch.json");
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        var firstBytes = "{\"first\":1}"u8.ToArray();
        var secondBytes = "{\"second\":2}"u8.ToArray();
        File.WriteAllBytes(first, firstBytes);
        File.WriteAllBytes(second, secondBytes);
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var firstStep = journal.BeginStep(InstallerRunEffectKind.ResidencyPolicy, "first");
        journal.CompleteStep(
            firstStep,
            "written",
            isReversible: true,
            new InstallerRunUndoData(Files: [CreatedFileUndo(first, firstBytes)]));
        var secondStep = journal.BeginStep(InstallerRunEffectKind.OllamaLaunchRecord, "second");
        journal.CompleteStep(
            secondStep,
            "written",
            isReversible: true,
            new InstallerRunUndoData(Files: [CreatedFileUndo(second, secondBytes)]));
        journal.Finish(InstallerRunOutcome.Failed);

        var result = await Rollback(NoProcessRunner()).RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal(
            [secondStep, firstStep],
            result.Steps.Select(step => step.StepId).ToArray());
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    private InstallerRunRollback Rollback(IProcessRunner runner) =>
        new(runner, layout, TimeSpan.FromSeconds(30));

    private InstallerRunJournal JournalWithActivation(string activated, string prior)
    {
        var journal = InstallerRunJournal.Start(journalDirectory);
        var step = journal.BeginStep(
            InstallerRunEffectKind.PackageActivation,
            $"LocalAi package {activated}");
        journal.CompleteStep(
            step,
            $"Activated {activated}; {prior} was active before.",
            isReversible: true,
            new InstallerRunUndoData(activated, prior));
        journal.Finish(InstallerRunOutcome.Failed);
        return journal;
    }

    private InstallerRunJournal JournalWithFileStep(InstallerRunFileUndo file)
    {
        var journal = InstallerRunJournal.Start(journalDirectory);
        var step = journal.BeginStep(
            InstallerRunEffectKind.ResidencyPolicy,
            "Journalled file effect");
        journal.CompleteStep(
            step,
            "written",
            isReversible: true,
            new InstallerRunUndoData(Files: [file]));
        journal.Finish(InstallerRunOutcome.Failed);
        return journal;
    }

    /// <summary>An undo record for a file the run created where nothing existed before.</summary>
    private static InstallerRunFileUndo CreatedFileUndo(string path, byte[] written) =>
        new(
            path,
            false,
            Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())),
            null,
            null,
            Convert.ToHexString(SHA256.HashData(written)));

    private void WritePointer(string version)
    {
        Directory.CreateDirectory(layout.BinRoot);
        File.WriteAllBytes(
            layout.CurrentPointerPath,
            CurrentPointerSnapshot.CreateCanonicalBytes(version));
    }

    private void WriteLauncher()
    {
        Directory.CreateDirectory(layout.LauncherDirectory);
        File.WriteAllBytes(layout.LauncherPath, [0x4D, 0x5A]);
    }

    private static RecordingRunner NoProcessRunner() =>
        new((_, _, _, _) => throw new InvalidOperationException(
            "No process should run for this rollback."));

    private sealed class RecordingRunner(
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessResult>> run)
        : IProcessRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add((executable, arguments.ToArray()));
            return run(executable, arguments, timeout, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
