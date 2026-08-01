using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Tests;

public sealed class RollbackServiceTests
{
    [Fact]
    public async Task Rollback_runs_completed_transactional_steps_in_reverse_and_verifies_restoration()
    {
        var calls = new List<string>();
        var service = new RollbackService();
        var snapshot = InstallerJournalSnapshot.Start(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            [
                InstallerJournalStep.Completed("dependency.git", transactional: false, artifactSha256: null),
                InstallerJournalStep.Completed("package.activate", transactional: true, artifactSha256: "P", backupPath: @"C:\backup\launcher.bak"),
                InstallerJournalStep.Completed("agent.codex", transactional: true, artifactSha256: "A", backupPath: @"C:\backup\codex.bak"),
            ],
            [new(
                "dependency.git",
                InstallerEffectKind.DependencyInstall,
                "Git install remains installed.")]);

        var result = await service.RollbackAsync(
            snapshot,
            step =>
            {
                calls.Add(step.StepId);
                return Task.FromResult(RollbackStepResult.Restored(step.StepId + "-verified"));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["agent.codex", "package.activate"], calls);
        Assert.True(result.Success);
        Assert.Contains(result.Instructions, instruction => instruction.Contains("Git install remains installed.", StringComparison.Ordinal));
        Assert.All(result.RestoredSteps, step => Assert.EndsWith("-verified", step.Verification));
    }

    [Fact]
    public async Task Rollback_reports_manual_recovery_when_verification_fails()
    {
        var service = new RollbackService();
        var snapshot = InstallerJournalSnapshot.Start(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            [InstallerJournalStep.Completed("agent.codex", transactional: true, artifactSha256: "A", backupPath: @"C:\backup\codex.bak")],
            []);

        var result = await service.RollbackAsync(
            snapshot,
            _ => Task.FromResult(RollbackStepResult.Failed("hash mismatch")),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(result.Instructions, instruction => instruction.Contains("agent.codex", StringComparison.Ordinal));
        Assert.Contains(result.Instructions, instruction => instruction.Contains(@"C:\backup\codex.bak", StringComparison.Ordinal));
    }
}
