using LocalAi.Contracts;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Transactions;

public sealed record InstallerExecutionPlan(
    IReadOnlyList<InstallerStepDefinition> StepDefinitions,
    IReadOnlyList<InstallerExecutionStep> ExecutionSteps,
    IReadOnlyList<NonTransactionalJournalEffect> NonTransactionalEffects);

public sealed class InstallerExecutionPlanFactory
{
    public delegate Task<IReadOnlyList<ModelRecommendationChoice>> ResolveModelFallbackChoices(
        ModelInstallAction action,
        CancellationToken cancellationToken = default);

    public delegate Task<AgentConfigurationPlan> PreviewAgentConfiguration(
        string agentActionId,
        AgentIntegrationChoice choice,
        string? configPath,
        string? instructionsPath,
        CancellationToken cancellationToken = default);

    public static InstallerExecutionPlan Create(
        InstallerPlan plan,
        Func<DependencyAction, CancellationToken, Task<InstallerStepResult>> executeDependency,
        Func<LocalAiPackageAction, CancellationToken, Task<InstallerStepResult>> executePackage,
        Func<BrokerModelInstallRequest, CancellationToken, Task<InstallerStepResult>> executeModel,
        ResolveModelFallbackChoices resolveModelFallbackChoices,
        PreviewAgentConfiguration previewAgent,
        Func<AgentConfigurationPlan, CancellationToken, Task> applyAgentConfiguration,
        Func<AgentConfigurationPlan, CancellationToken, Task> rollbackAgentConfiguration)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        ArgumentNullException.ThrowIfNull(executeDependency);
        ArgumentNullException.ThrowIfNull(executePackage);
        ArgumentNullException.ThrowIfNull(executeModel);
        ArgumentNullException.ThrowIfNull(resolveModelFallbackChoices);
        ArgumentNullException.ThrowIfNull(previewAgent);
        ArgumentNullException.ThrowIfNull(applyAgentConfiguration);
        ArgumentNullException.ThrowIfNull(rollbackAgentConfiguration);

        var definitions = new List<InstallerStepDefinition>();
        var execution = new List<InstallerExecutionStep>();
        var manualEffects = BuildManualEffects(plan);

        foreach (var dependency in plan.Dependencies)
        {
            if (!dependency.Selected)
            {
                continue;
            }

            definitions.Add(new InstallerStepDefinition(
                dependency.ActionId,
                InstallerEffectKind.DependencyInstall,
                false));
            execution.Add(InstallerExecutionStep.NonTransactional(
                dependency.ActionId,
                ct => executeDependency(dependency, ct)));
        }

        if (plan.Package.Selected)
        {
            definitions.Add(
                new InstallerStepDefinition(
                    plan.Package.ActionId,
                    InstallerEffectKind.PackageActivation,
                    true));
            execution.Add(InstallerExecutionStep.Transactional(
                plan.Package.ActionId,
                ct => executePackage(plan.Package, ct),
                static (_, __) => throw new NotSupportedException(
                    "Package activation rollback requires a dedicated recovery workflow.")));
        }

        foreach (var model in plan.Models)
        {
            if (!model.Selected)
            {
                continue;
            }

            definitions.Add(
                new InstallerStepDefinition(
                    model.ActionId,
                    InstallerEffectKind.ModelInstall,
                    false));
            execution.Add(InstallerExecutionStep.NonTransactional(
                model.ActionId,
                async ct =>
                {
                    var fallback = await resolveModelFallbackChoices(model, ct);
                    return await executeModel(
                        new BrokerModelInstallRequest(model, fallback),
                        ct);
                }));
        }

        foreach (var agent in plan.Agents)
        {
            if (!agent.Selected)
            {
                continue;
            }

            definitions.Add(
                new InstallerStepDefinition(
                    agent.ActionId,
                    InstallerEffectKind.AgentConfiguration,
                    true));
            AgentConfigurationPlan? appliedPlan = null;
            execution.Add(InstallerExecutionStep.Transactional(
                agent.ActionId,
                async ct =>
                {
                    var planned = await previewAgent(
                        agent.ActionId,
                        agent.Choice,
                        agent.ConfigPath,
                        agent.InstructionsPath,
                        ct);

                    if (planned.HasChanges)
                    {
                        await applyAgentConfiguration(planned, ct);
                        appliedPlan = planned;
                    }

                    return InstallerStepResult.Completed(
                        planned.AgentName,
                        backupPath: planned.Files.Select(file => file.BackupPath)
                            .FirstOrDefault());
                },
                async (_, ct) =>
                {
                    if (appliedPlan is null)
                    {
                        return;
                    }

                    await rollbackAgentConfiguration(appliedPlan, ct);
                }));
        }

        return new InstallerExecutionPlan(
            definitions,
            execution,
            manualEffects);
    }

    public static IReadOnlyList<NonTransactionalJournalEffect> BuildManualEffects(InstallerPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var actionKinds = new Dictionary<string, InstallerEffectKind>(StringComparer.Ordinal);
        foreach (var dependency in plan.Dependencies)
        {
            actionKinds[dependency.ActionId] = InstallerEffectKind.DependencyInstall;
        }

        foreach (var model in plan.Models)
        {
            actionKinds[model.ActionId] = InstallerEffectKind.ModelInstall;
        }

        var effects = new List<NonTransactionalJournalEffect>();
        foreach (var effect in plan.NonTransactionalEffects)
        {
            effects.Add(new(
                effect.ActionId,
                actionKinds.TryGetValue(effect.RelatedActionId, out var kind)
                    ? kind
                    : throw new InvalidOperationException(
                        $"Unknown related action '{effect.RelatedActionId}' for non-transactional effect."),
                effect.Description));
        }

        return effects;
    }
}
