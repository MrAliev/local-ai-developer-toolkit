namespace LocalAi.Installer.Core.Transactions;

public interface IInstallerStep
{
    string Id { get; }

    bool IsTransactional { get; }

    Task<InstallerStepResult> ExecuteAsync(CancellationToken cancellationToken);

    Task RollbackAsync(InstallerJournalStep step, CancellationToken cancellationToken);
}

public enum InstallerExecutionStatus
{
    Completed,
    Failed,
}

public sealed record InstallerExecutionResult(
    InstallerExecutionStatus Status,
    string? FailedStepId,
    string? FailureCode);

public sealed class InstallerExecutionStep : IInstallerStep
{
    private readonly Func<CancellationToken, Task<InstallerStepResult>> execute;
    private readonly Func<InstallerJournalStep, CancellationToken, Task> rollback;

    private InstallerExecutionStep(
        string id,
        bool isTransactional,
        Func<CancellationToken, Task<InstallerStepResult>> execute,
        Func<InstallerJournalStep, CancellationToken, Task>? rollback)
    {
        Id = id;
        IsTransactional = isTransactional;
        this.execute = execute;
        this.rollback = rollback ?? ((_, _) => Task.CompletedTask);
    }

    public string Id { get; }

    public bool IsTransactional { get; }

    public static InstallerExecutionStep Transactional(
        string id,
        Func<CancellationToken, Task<InstallerStepResult>> execute) =>
        new(id, true, execute, null);

    public static InstallerExecutionStep Transactional(
        string id,
        Func<CancellationToken, Task<InstallerStepResult>> execute,
        Func<InstallerJournalStep, CancellationToken, Task> rollback) =>
        new(id, true, execute, rollback);

    public static InstallerExecutionStep NonTransactional(
        string id,
        Func<CancellationToken, Task<InstallerStepResult>> execute) =>
        new(id, false, execute, null);

    public Task<InstallerStepResult> ExecuteAsync(CancellationToken cancellationToken) =>
        execute(cancellationToken);

    public Task RollbackAsync(InstallerJournalStep step, CancellationToken cancellationToken) =>
        rollback(step, cancellationToken);
}

public sealed class InstallerExecutor
{
    private readonly InstallerJournal journal;
    private readonly IReadOnlyDictionary<string, IInstallerStep>? steps;

    public InstallerExecutor(InstallerJournal journal)
    {
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public InstallerExecutor(
        InstallerJournal journal,
        IReadOnlyList<IInstallerStep> steps)
    {
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.steps = (steps ?? throw new ArgumentNullException(nameof(steps)))
            .ToDictionary(step => step.Id, StringComparer.Ordinal);
    }

    public async Task<InstallerExecutionResult> ExecuteAsync(
        Guid planId,
        IReadOnlyList<InstallerExecutionStep> executionSteps,
        CancellationToken cancellationToken = default)
    {
        if (journal.Snapshot.PlanId != planId)
        {
            journal.Snapshot = journal.Snapshot with { PlanId = planId };
        }

        var runner = new InstallerExecutor(journal, executionSteps);
        return await runner.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstallerExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (var journalStep in journal.Snapshot.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journalStep.Status is InstallerStepStatus.Completed or InstallerStepStatus.RolledBack)
            {
                continue;
            }

            if (steps is null || !steps.TryGetValue(journalStep.StepId, out var step))
            {
                throw new InvalidOperationException("No executor step is registered for the journal step.");
            }

            try
            {
                await journal.RecordRunningAsync(journalStep.StepId, cancellationToken)
                    .ConfigureAwait(false);
                var result = await step.ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await journal.RecordCompletedAsync(journalStep.StepId, result, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InstallerStepException exception)
            {
                await journal.RecordFailedAsync(
                        journalStep.StepId,
                        exception.Code,
                        exception.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    InstallerExecutionStatus.Failed,
                    journalStep.StepId,
                    exception.Code);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                const string code = "installer_step_failed";
                await journal.RecordFailedAsync(
                        journalStep.StepId,
                        code,
                        "Installer step failed.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    InstallerExecutionStatus.Failed,
                    journalStep.StepId,
                    code);
            }
        }

        return new(InstallerExecutionStatus.Completed, null, null);
    }
}
