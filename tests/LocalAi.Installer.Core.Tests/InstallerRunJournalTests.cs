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
        var journal = InstallerRunJournal.Start(directory);
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
        var journal = InstallerRunJournal.Start(directory);
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
        var finished = InstallerRunJournal.Start(directory);
        finished.Finish(InstallerRunOutcome.Completed);
        var interrupted = InstallerRunJournal.Start(directory);
        interrupted.BeginStep(
            InstallerRunEffectKind.DependencyInstall,
            "Prerequisite Git (Git.Git)");

        var found = InstallerRunJournal.FindInterrupted(directory);

        Assert.NotNull(found);
        Assert.Equal(interrupted.Snapshot.RunId, found.Snapshot.RunId);
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
            InstallerRunJournal.Start(directory).Finish(outcome);
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
        var intact = InstallerRunJournal.Start(directory);
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
        var journal = InstallerRunJournal.Start(directory);
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
        var journal = InstallerRunJournal.Start(directory);
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
