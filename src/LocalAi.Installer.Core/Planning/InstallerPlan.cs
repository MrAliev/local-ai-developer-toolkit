using System.Collections.ObjectModel;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Planning;

public enum AgentIntegrationChoice
{
    McpOnly,
    InstructionsOnly,
    McpAndInstructions,
    NoChange,
}

public sealed record DependencyAction(
    string ActionId,
    string PackageId,
    string Version,
    bool Selected,
    bool ConsentGranted);

public sealed record LocalAiPackageAction(
    string ActionId,
    string Version,
    string PackagePath,
    bool Selected,
    bool ConsentGranted);

public sealed record ModelInstallAction(
    string ActionId,
    string Model,
    int ContextSize,
    bool Selected,
    bool ConsentGranted);

public sealed record AgentConfigurationAction(
    string ActionId,
    AgentKind AgentKind,
    AgentIntegrationChoice Choice,
    string? ConfigPath,
    string? InstructionsPath,
    bool Selected,
    bool ConsentGranted);

public sealed record NonTransactionalEffect(
    string ActionId,
    string RelatedActionId,
    string Description);

public sealed record InstallerPlan
{
    public InstallerPlan(
        Guid planId,
        DateTimeOffset createdAtUtc,
        EnvironmentDiagnosis diagnosis,
        IReadOnlyList<DependencyAction> dependencies,
        LocalAiPackageAction package,
        IReadOnlyList<ModelInstallAction> models,
        IReadOnlyList<AgentConfigurationAction> agents,
        IReadOnlyList<NonTransactionalEffect> nonTransactionalEffects)
    {
        PlanId = planId;
        CreatedAtUtc = createdAtUtc;
        Diagnosis = diagnosis ?? throw new ArgumentNullException(nameof(diagnosis));
        Dependencies = Snapshot(dependencies);
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Models = Snapshot(models);
        Agents = Snapshot(agents);
        NonTransactionalEffects = Snapshot(nonTransactionalEffects);
    }

    public Guid PlanId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public EnvironmentDiagnosis Diagnosis { get; }
    public IReadOnlyList<DependencyAction> Dependencies { get; }
    public LocalAiPackageAction Package { get; }
    public IReadOnlyList<ModelInstallAction> Models { get; }
    public IReadOnlyList<AgentConfigurationAction> Agents { get; }
    public IReadOnlyList<NonTransactionalEffect> NonTransactionalEffects { get; }

    private static ReadOnlyCollection<T> Snapshot<T>(IReadOnlyList<T> source) =>
        Array.AsReadOnly(
            (source ?? throw new ArgumentNullException(nameof(source))).ToArray());
}
