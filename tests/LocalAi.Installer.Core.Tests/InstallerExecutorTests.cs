using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Tests;

public sealed class InstallerExecutorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-journal-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Journal_uses_strict_schema_and_atomic_snapshots_without_sensitive_contents()
    {
        var journal = new InstallerJournal(root, new FixedTimeProvider(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero)));
        var snapshot = InstallerJournalSnapshot.Start(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            [InstallerJournalStep.Pending("package.activate", transactional: true)],
            [new JournalNonTransactionalEffect("dependency.git", "Git install cannot be rolled back automatically.")]);

        await journal.SaveAsync(snapshot, TestContext.Current.CancellationToken);
        var loaded = await journal.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal("package.activate", Assert.Single(loaded.Steps).StepId);
        Assert.DoesNotContain("secret", File.ReadAllText(journal.Path), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(journal.Path + ".tmp"));

        File.WriteAllText(journal.Path, "{\"schemaVersion\":2,\"planId\":\"11111111-1111-1111-1111-111111111111\",\"updatedAtUtc\":\"2026-07-31T12:00:00Z\",\"steps\":[]}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Executor_resumes_completed_steps_and_retries_failed_steps_idempotently()
    {
        var calls = new List<string>();
        var journal = new InstallerJournal(root, TimeProvider.System);
        var snapshot = InstallerJournalSnapshot.Start(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            [
                InstallerJournalStep.Completed("dependency.git", transactional: false, artifactSha256: "A"),
                InstallerJournalStep.Failed("package.activate", transactional: true, "timeout"),
                InstallerJournalStep.Pending("agent.codex", transactional: true),
            ],
            []);
        await journal.SaveAsync(snapshot, TestContext.Current.CancellationToken);
        var executor = new InstallerExecutor(journal);

        await executor.ExecuteAsync(
            snapshot.PlanId,
            [
                InstallerExecutionStep.NonTransactional("dependency.git", _ =>
                {
                    calls.Add("dependency.git");
                    return Task.FromResult(InstallerStepResult.Completed("A", null));
                }),
                InstallerExecutionStep.Transactional("package.activate", _ =>
                {
                    calls.Add("package.activate");
                    return Task.FromResult(InstallerStepResult.Completed("B", @"C:\backup\launcher.bak"));
                }),
                InstallerExecutionStep.Transactional("agent.codex", _ =>
                {
                    calls.Add("agent.codex");
                    return Task.FromResult(InstallerStepResult.Completed("C", @"C:\backup\codex.bak"));
                }),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(["package.activate", "agent.codex"], calls);
        var loaded = await journal.LoadAsync(TestContext.Current.CancellationToken);
        Assert.All(loaded.Steps, step => Assert.Equal(InstallerStepStatus.Completed, step.Status));
        Assert.Equal(@"C:\backup\launcher.bak", loaded.Steps.Single(step => step.StepId == "package.activate").BackupPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
