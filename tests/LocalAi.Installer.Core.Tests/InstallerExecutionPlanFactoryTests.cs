using System.Runtime.InteropServices;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Planning;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Tests;

public sealed class InstallerExecutionPlanFactoryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-installer-plan-factory-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Create_builds_ordered_execution_plan_with_explicit_effect_kinds_and_rollback_hook()
    {
        var calls = new List<string>();

        var executionPlan = BuildFromSamplePlan(
            async (_, _) =>
            {
                calls.Add("dependency");
                return InstallerStepResult.Completed("A", null);
            },
            async (_, _) =>
            {
                calls.Add("package");
                return InstallerStepResult.Completed("B", null);
            },
            async (_, _) =>
            {
                calls.Add("model");
                return InstallerStepResult.Completed("C", null);
            },
            async (_, _) =>
            {
                calls.Add("model.fallback");
                return Array.Empty<ModelRecommendationChoice>();
            },
            async (_, _, _, _, _) =>
            {
                calls.Add("preview");
                return new AgentConfigurationPlan(
                    "Codex",
                    [
                        new AgentConfigurationFilePlan(
                            Path.Combine(root, ".codex", "config.toml"),
                            [],
                            [],
                            string.Empty,
                            string.Empty,
                            Path.Combine(root, ".codex", "config.toml.bak")),
                    ],
                    "Agent preview");
            },
            async (_, _) =>
            {
                calls.Add("apply");
                await Task.CompletedTask;
            },
            async (_, _) =>
            {
                calls.Add("rollback");
                await Task.CompletedTask;
            });

        var snapshot = BuildSnapshotFromPlan(executionPlan);
        var journal = new InstallerJournal(root, TimeProvider.System);
        await journal.SaveAsync(snapshot, TestContext.Current.CancellationToken);

        var executor = new InstallerExecutor(journal, executionPlan.ExecutionSteps);
        var result = await executor.ExecuteAsync(
            snapshot.PlanId,
            executionPlan.ExecutionSteps,
            TestContext.Current.CancellationToken);

        Assert.Equal(InstallerExecutionStatus.Completed, result.Status);
        Assert.Equal(
            ["dependency", "package", "model.fallback", "model", "preview", "apply"],
            calls);

        var steps = executionPlan.StepDefinitions.ToArray();
        Assert.Equal(
            ["dependency.git", "package.localai", "model.qwen", "agent.codex"],
            steps.Select(step => step.StepId).ToArray());
        Assert.Equal(
            [
                InstallerEffectKind.DependencyInstall,
                InstallerEffectKind.PackageActivation,
                InstallerEffectKind.ModelInstall,
                InstallerEffectKind.AgentConfiguration,
            ],
            steps.Select(step => step.EffectKind).ToArray());
        Assert.Equal([false, true, false, true], steps.Select(step => step.IsTransactional).ToArray());

        var loaded = await journal.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [InstallerEffectKind.DependencyInstall, InstallerEffectKind.ModelInstall],
            loaded.NonTransactionalEffects.Select(effect => effect.EffectKind).ToArray());

        var rollbackStep = executionPlan.ExecutionSteps.Single(step => step.Id == "agent.codex");
        await rollbackStep.RollbackAsync(
            loaded.Steps.Single(step => step.StepId == "agent.codex"),
            TestContext.Current.CancellationToken);
        Assert.Contains("rollback", calls);
    }

    [Fact]
    public void BuildManualEffects_maps_dependency_and_model_actions()
    {
        var plan = BuildSamplePlan();
        var effects = InstallerExecutionPlanFactory.BuildManualEffects(plan);

        Assert.Equal(2, effects.Count);
        Assert.Equal(
            InstallerEffectKind.DependencyInstall,
            effects.Single(effect => effect.StepId == "effect.dependency").EffectKind);
        Assert.Equal(
            InstallerEffectKind.ModelInstall,
            effects.Single(effect => effect.StepId == "effect.model").EffectKind);
    }

    [Fact]
    public void BuildManualEffects_rejects_unknown_related_action()
    {
        var plan = BuildSamplePlan();
        var invalid = plan.NonTransactionalEffects
            .Select(effect =>
                new NonTransactionalEffect(effect.ActionId, "missing.action", effect.Description))
            .ToArray();

        Assert.Throws<ArgumentException>(() =>
            new InstallerPlanBuilder(TimeProvider.System, () => plan.PlanId).Build(
                plan.Diagnosis,
                plan.Dependencies,
                plan.Package,
                plan.Models,
                plan.Agents,
                invalid));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static InstallerExecutionPlan BuildFromSamplePlan(
        Func<DependencyAction, CancellationToken, Task<InstallerStepResult>> executeDependency,
        Func<LocalAiPackageAction, CancellationToken, Task<InstallerStepResult>> executePackage,
        Func<BrokerModelInstallRequest, CancellationToken, Task<InstallerStepResult>> executeModel,
        InstallerExecutionPlanFactory.ResolveModelFallbackChoices resolveModelFallbackChoices,
        InstallerExecutionPlanFactory.PreviewAgentConfiguration previewAgent,
        Func<AgentConfigurationPlan, CancellationToken, Task> applyAgentConfiguration,
        Func<AgentConfigurationPlan, CancellationToken, Task> rollbackAgentConfiguration)
    {
        return InstallerExecutionPlanFactory.Create(
            BuildSamplePlan(),
            executeDependency,
            executePackage,
            executeModel,
            resolveModelFallbackChoices,
            previewAgent,
            applyAgentConfiguration,
            rollbackAgentConfiguration);
    }

    private static InstallerJournalSnapshot BuildSnapshotFromPlan(InstallerExecutionPlan plan)
    {
        var steps = plan.StepDefinitions
            .Select(step => InstallerJournalStep.Pending(step.StepId, step.EffectKind, step.IsTransactional))
            .ToArray();
        var effects = plan.NonTransactionalEffects
            .Select(effect => new JournalNonTransactionalEffect(
                effect.StepId,
                effect.EffectKind,
                effect.Description))
            .ToArray();
        return InstallerJournalSnapshot.Start(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            steps,
            effects);
    }

    private static InstallerPlan BuildSamplePlan()
    {
        var builder = new InstallerPlanBuilder(
            TimeProvider.System,
            () => Guid.Parse("11111111-1111-1111-1111-111111111111"));

        return builder.Build(
            SupportedDiagnosis(),
            [
                new(
                    "dependency.git",
                    "Git.Git",
                    "2.50.1",
                    true,
                    true),
            ],
            new(
                "package.localai",
                "1.2.3",
                @"C:\Downloads\localai.zip",
                true,
                true),
            [
                new(
                    "model.qwen",
                    "qwen3:8b",
                    32_768,
                    true,
                    true),
            ],
            [
                new(
                    "agent.codex",
                    AgentKind.Codex,
                    AgentIntegrationChoice.McpOnly,
                    @"C:\Users\test\.codex\config.toml",
                    null,
                    true,
                    true),
            ],
            [
                new("effect.dependency", "dependency.git", "Dependency remains"),
                new("effect.model", "model.qwen", "Model remains"),
            ]);
    }

    private static EnvironmentDiagnosis SupportedDiagnosis() =>
        new(
            new OperatingSystemSnapshot(
                "Windows 11 Pro",
                new Version(10, 0, 26100),
                Architecture.X64,
                SupportStatus.Supported,
                SupportStatus.Supported),
            new DiskSnapshot(ObservationState.Available, 100_000, null),
            new NetworkSnapshot(ObservationState.Available, null),
            new DependencySnapshot(
                "WinGet",
                DependencyState.Detected,
                @"C:\Windows\winget.exe",
                "1.10",
                null),
            new DependencySnapshot(
                "Git",
                DependencyState.Detected,
                @"C:\Program Files\Git\bin\git.exe",
                "2.50",
                null),
            new DependencySnapshot(
                "GitHubCli",
                DependencyState.Detected,
                @"C:\Program Files\GitHub CLI\gh.exe",
                "2.97",
                null),
            new DependencySnapshot(
                "Ollama",
                DependencyState.NotFound,
                null,
                null,
                null),
            new GpuSnapshot(
                ObservationState.Available,
                [new GpuAdapterSnapshot("gpu-1", "GPU 1", 8_000, false)],
                null),
            new ExistingLocalAiSnapshot(
                ExistingLocalAiState.Absent,
                null,
                null,
                null),
            [],
            []);
}
