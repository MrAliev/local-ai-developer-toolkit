namespace LocalAi.Installer.Core.Transactions;

public enum RollbackStatus
{
    Completed,
    CompletedWithManualFollowup,
    Failed,
}

public sealed record RollbackResult(
    RollbackStatus Status,
    IReadOnlyList<string> ManualInstructions);

public sealed record RestoredRollbackStep(string StepId, string Verification);

public sealed record RollbackStepResult(bool Success, string? Verification, string? Failure)
{
    public static RollbackStepResult Restored(string verification) =>
        new(true, verification, null);

    public static RollbackStepResult Failed(string failure) =>
        new(false, null, failure);
}

public sealed record RollbackPlanResult(
    bool Success,
    IReadOnlyList<RestoredRollbackStep> RestoredSteps,
    IReadOnlyList<string> Instructions);

public sealed class RollbackService
{
    private readonly InstallerJournal? journal;
    private readonly IReadOnlyDictionary<string, IInstallerStep>? steps;

    public RollbackService()
    {
    }

    public RollbackService(
        InstallerJournal journal,
        IReadOnlyList<IInstallerStep> steps)
    {
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.steps = (steps ?? throw new ArgumentNullException(nameof(steps)))
            .ToDictionary(step => step.Id, StringComparer.Ordinal);
    }

    public async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (journal is null || steps is null)
        {
            throw new InvalidOperationException("Rollback journal and steps were not configured.");
        }

        var manual = new List<string>();
        foreach (var journalStep in journal.Snapshot.Steps
                     .Where(step => step.Status == InstallerStepStatus.Completed)
                     .Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!steps.TryGetValue(journalStep.StepId, out var step))
            {
                throw new InvalidOperationException("No rollback step is registered for the journal step.");
            }

            if (!journalStep.IsTransactional || !step.IsTransactional)
            {
                manual.Add(
                    $"Step '{journalStep.StepId}' is non-transactional; inspect external {journalStep.EffectKind} state manually.");
                continue;
            }

            try
            {
                await step.RollbackAsync(journalStep, cancellationToken)
                    .ConfigureAwait(false);
                await journal.RecordRolledBackAsync(journalStep.StepId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await journal.RecordRollbackFailedAsync(
                        journalStep.StepId,
                        "rollback_failed",
                        cancellationToken)
                    .ConfigureAwait(false);
                manual.Add(
                    $"Rollback failed for '{journalStep.StepId}'. Use journal '{journal.JournalPath}', recorded hashes, and backup paths to restore manually.");
                return new(RollbackStatus.Failed, manual);
            }
        }

        foreach (var effect in journal.Snapshot.NonTransactionalEffects)
        {
            if (!manual.Any(instruction => instruction.Contains(effect.StepId, StringComparison.Ordinal)))
            {
                manual.Add(
                    $"Step '{effect.StepId}' is non-transactional; {effect.Description}");
            }
        }

        return manual.Count == 0
            ? new(RollbackStatus.Completed, manual)
            : new(RollbackStatus.CompletedWithManualFollowup, manual);
    }

    public async Task<RollbackPlanResult> RollbackAsync(
        InstallerJournalSnapshot snapshot,
        Func<InstallerJournalStep, Task<RollbackStepResult>> restore,
        CancellationToken cancellationToken = default)
    {
        var restored = new List<RestoredRollbackStep>();
        var instructions = snapshot.NonTransactionalEffects
            .Select(effect => effect.Description)
            .ToList();
        foreach (var step in snapshot.Steps
                     .Where(step => step.Status == InstallerStepStatus.Completed && step.IsTransactional)
                     .Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await restore(step).ConfigureAwait(false);
            if (!result.Success)
            {
                instructions.Add(
                    $"Rollback failed for {step.StepId}; restore from {step.BackupPath ?? "recorded backup path"} and verify {step.ArtifactSha256 ?? "recorded hash"} manually.");
                return new(false, restored, instructions);
            }

            restored.Add(new RestoredRollbackStep(step.StepId, result.Verification ?? "verified"));
        }

        return new(true, restored, instructions);
    }
}
