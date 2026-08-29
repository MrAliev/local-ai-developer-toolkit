using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Tests;

public sealed class InstallerRunJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.RunJournal.Tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The failure this journal exists for is the process dying between the intent and the
    /// outcome, so the intent has to be on disk before the effect runs — not in memory
    /// waiting for a flush that never comes.
    /// </summary>
    [Fact]
    public void The_intent_is_on_disk_before_the_effect_completes()
    {
        using var journal = InstallerRunJournal.Start(directory);
        var stepId = journal.BeginStep(
            InstallerRunEffectKind.AgentConfiguration,
            "Claude client configuration");

        // Another reader — the next wizard run — sees the running step already.
        var reloaded = InstallerRunJournal.Load(journal.JournalPath);

        var step = Assert.Single(reloaded.Snapshot.Steps);
        Assert.Equal(stepId, step.StepId);
        Assert.Equal(InstallerRunStepStatus.Running, step.Status);
        Assert.Null(reloaded.Snapshot.Outcome);
        Assert.True(reloaded.Snapshot.IsInterrupted);
    }

    [Fact]
    public void A_completed_step_keeps_its_undo_data_across_reload()
    {
        using var journal = InstallerRunJournal.Start(directory);
        var stepId = journal.BeginStep(
            InstallerRunEffectKind.PackageActivation,
            "LocalAi package 0.2.0");
        journal.CompleteStep(
            stepId,
            "Activated 0.2.0; 0.1.9 was active before.",
            isReversible: true,
            new InstallerRunUndoData("0.2.0", "0.1.9"));
        journal.Finish(InstallerRunOutcome.Failed);

        var reloaded = InstallerRunJournal.Load(journal.JournalPath);

        var step = Assert.Single(reloaded.Snapshot.Steps);
        Assert.Equal(InstallerRunStepStatus.Completed, step.Status);
        Assert.True(step.IsReversible);
        Assert.Equal("0.2.0", step.Undo?.ActivatedVersion);
        Assert.Equal("0.1.9", step.Undo?.PriorVersion);
        Assert.Equal(InstallerRunOutcome.Failed, reloaded.Snapshot.Outcome);
        Assert.True(reloaded.Snapshot.HasReversibleWork);
    }

    [Fact]
    public void Find_interrupted_returns_the_run_that_never_wrote_an_outcome()
    {
        using var finished = InstallerRunJournal.Start(directory);
        finished.Finish(InstallerRunOutcome.Completed);
        using var interrupted = InstallerRunJournal.Start(directory);
        interrupted.BeginStep(
            InstallerRunEffectKind.DependencyInstall,
            "Prerequisite Git (Git.Git)");
        // Disposing releases the live lock the way process death does; without it the
        // journal belongs to a run that is still alive and must not be offered back.
        interrupted.Dispose();

        var found = InstallerRunJournal.FindInterrupted(directory);

        Assert.NotNull(found);
        Assert.Equal(interrupted.Snapshot.RunId, found.Snapshot.RunId);
    }

    /// <summary>
    /// An outcome of null alone cannot tell a killed wizard from one still installing in
    /// another window. The live lock can: it is held for exactly as long as the owning
    /// process lives, so a second wizard sees "alive, leave it" and the next start after a
    /// kill sees "dead, offer it back" — a condition, where any elapsed-time rule would
    /// call a slow install dead.
    /// </summary>
    [Fact]
    public void A_run_still_holding_its_live_lock_is_not_reported_as_interrupted()
    {
        using var journal = InstallerRunJournal.Start(directory);
        journal.BeginStep(
            InstallerRunEffectKind.PackageActivation,
            "LocalAi package 0.2.0");

        Assert.Null(InstallerRunJournal.FindInterrupted(directory));

        journal.Dispose();

        Assert.Equal(
            journal.Snapshot.RunId,
            InstallerRunJournal.FindInterrupted(directory)?.Snapshot.RunId);
    }

    /// <summary>
    /// A power loss skips DeleteOnClose, so the lock file can outlive its process. A lock
    /// nobody holds proves nothing is alive: the run is still offered back, and the
    /// leftover is cleaned up rather than allowed to hide the interruption forever.
    /// </summary>
    [Fact]
    public void A_stale_live_lock_from_a_power_loss_does_not_hide_the_run()
    {
        using var journal = InstallerRunJournal.Start(directory);
        journal.Dispose();
        var stale = journal.JournalPath + ".lock";
        File.WriteAllBytes(stale, []);

        var found = InstallerRunJournal.FindInterrupted(directory);

        Assert.Equal(journal.Snapshot.RunId, found?.Snapshot.RunId);
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void Finished_runs_are_not_reported_as_interrupted()
    {
        foreach (var outcome in new[]
                 {
                     InstallerRunOutcome.Completed,
                     InstallerRunOutcome.Failed,
                     InstallerRunOutcome.Cancelled,
                     InstallerRunOutcome.RolledBack,
                     InstallerRunOutcome.Abandoned,
                 })
        {
            using var journal = InstallerRunJournal.Start(directory);
            journal.Finish(outcome);
        }

        Assert.Null(InstallerRunJournal.FindInterrupted(directory));
    }

    /// <summary>
    /// A corrupt journal is a machine that already lost data once; refusing to install on
    /// top of it would turn one failure into two. It is skipped, and an intact interrupted
    /// journal behind it is still found.
    /// </summary>
    [Fact]
    public void A_corrupt_journal_is_skipped_not_fatal()
    {
        using var intact = InstallerRunJournal.Start(directory);
        intact.Dispose();
        // Named to sort after the intact file, so the scan meets the garbage first.
        File.WriteAllText(Path.Combine(directory, "journal-99999999-999999-zz.json"), "{not json");

        var found = InstallerRunJournal.FindInterrupted(directory);

        Assert.NotNull(found);
        Assert.Equal(intact.Snapshot.RunId, found.Snapshot.RunId);
    }

    [Fact]
    public void A_missing_directory_means_no_interrupted_run()
    {
        Assert.Null(InstallerRunJournal.FindInterrupted(
            Path.Combine(directory, "never-created")));
    }

    [Fact]
    public void Undo_outcomes_accept_only_undo_statuses()
    {
        using var journal = InstallerRunJournal.Start(directory);
        var stepId = journal.BeginStep(
            InstallerRunEffectKind.ResidencyPolicy,
            "Model residency policy");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            journal.MarkUndoOutcome(stepId, InstallerRunStepStatus.Completed, "no"));

        journal.CompleteStep(stepId, "written", isReversible: true);
        journal.MarkUndoOutcome(stepId, InstallerRunStepStatus.Undone, "restored");

        var reloaded = InstallerRunJournal.Load(journal.JournalPath);
        Assert.Equal(InstallerRunStepStatus.Undone, Assert.Single(reloaded.Snapshot.Steps).Status);
    }

    /// <summary>
    /// Only completed reversible steps are rollback's business. A failed step recorded
    /// nothing to undo, and a running one recorded an effect in an unknown state.
    /// </summary>
    [Fact]
    public void Reversible_work_requires_a_completed_reversible_step()
    {
        using var journal = InstallerRunJournal.Start(directory);
        var failed = journal.BeginStep(
            InstallerRunEffectKind.AgentConfiguration,
            "Codex client configuration");
        journal.FailStep(failed, "refused");
        journal.BeginStep(
            InstallerRunEffectKind.DependencyInstall,
            "Prerequisite Git (Git.Git)");

        Assert.False(journal.Snapshot.HasReversibleWork);

        var completed = journal.BeginStep(
            InstallerRunEffectKind.ResidencyPolicy,
            "Model residency policy");
        journal.CompleteStep(completed, "written", isReversible: true);

        Assert.True(journal.Snapshot.HasReversibleWork);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
