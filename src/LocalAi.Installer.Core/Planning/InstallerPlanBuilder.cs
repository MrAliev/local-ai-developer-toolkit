using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Planning;

public sealed class InstallerPlanBuilder
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _planIdFactory;

    public InstallerPlanBuilder(
        TimeProvider timeProvider,
        Func<Guid> planIdFactory)
    {
        _timeProvider =
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _planIdFactory =
            planIdFactory ?? throw new ArgumentNullException(nameof(planIdFactory));
    }

    public InstallerPlan Build(
        EnvironmentDiagnosis diagnosis,
        IReadOnlyList<DependencyAction> dependencies,
        LocalAiPackageAction package,
        IReadOnlyList<ModelInstallAction> models,
        IReadOnlyList<AgentConfigurationAction> agents,
        IReadOnlyList<NonTransactionalEffect> nonTransactionalEffects)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(nonTransactionalEffects);

        if (!diagnosis.IsSupported)
        {
            throw new InvalidOperationException(
                "An installer plan cannot be created for an unsupported environment.");
        }

        var dependencySnapshot = dependencies.ToArray();
        var modelSnapshot = models.ToArray();
        var agentSnapshot = agents.ToArray();
        var effectSnapshot = nonTransactionalEffects.ToArray();

        ValidateDependencies(dependencySnapshot);
        ValidatePackage(package);
        ValidateModels(modelSnapshot);
        ValidateAgents(agentSnapshot);
        ValidateUniqueActionIds(
            dependencySnapshot,
            package,
            modelSnapshot,
            agentSnapshot,
            effectSnapshot);
        ValidateEffects(
            effectSnapshot,
            dependencySnapshot,
            modelSnapshot);

        var planId = _planIdFactory();
        if (planId == Guid.Empty)
        {
            throw new InvalidOperationException("The plan ID cannot be empty.");
        }

        var createdAtUtc = _timeProvider.GetUtcNow();
        if (createdAtUtc.Offset != TimeSpan.Zero ||
            createdAtUtc < DateTimeOffset.UnixEpoch)
        {
            throw new InvalidOperationException(
                "The plan creation time must be a valid UTC timestamp.");
        }

        return new InstallerPlan(
            planId,
            createdAtUtc,
            SnapshotDiagnosis(diagnosis),
            dependencySnapshot,
            package with { },
            modelSnapshot,
            agentSnapshot,
            effectSnapshot);
    }

    private static void ValidateDependencies(
        IReadOnlyList<DependencyAction> dependencies)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in dependencies)
        {
            ArgumentNullException.ThrowIfNull(action);
            ValidateActionId(action.ActionId);
            ValidateRequiredToken(action.PackageId, nameof(action.PackageId));
            ValidateRequiredToken(action.Version, nameof(action.Version));
            ValidateConsent(action.Selected, action.ConsentGranted, action.ActionId);

            if (!packageIds.Add(action.PackageId))
            {
                throw new ArgumentException(
                    $"Dependency package ID '{action.PackageId}' is selected more than once.",
                    nameof(dependencies));
            }
        }
    }

    private static void ValidatePackage(LocalAiPackageAction package)
    {
        ValidateActionId(package.ActionId);
        ValidateRequiredToken(package.Version, nameof(package.Version));
        ValidateAbsolutePath(package.PackagePath, nameof(package.PackagePath));
        ValidateConsent(
            package.Selected,
            package.ConsentGranted,
            package.ActionId);
    }

    private static void ValidateModels(
        IReadOnlyList<ModelInstallAction> models)
    {
        var selections = new HashSet<ModelSelection>(ModelSelectionComparer.Instance);
        foreach (var action in models)
        {
            ArgumentNullException.ThrowIfNull(action);
            ValidateActionId(action.ActionId);
            ValidateRequiredToken(action.Model, nameof(action.Model));
            if (action.ContextSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(action.ContextSize),
                    action.ContextSize,
                    "Model context size must be positive.");
            }

            ValidateConsent(action.Selected, action.ConsentGranted, action.ActionId);
            if (!selections.Add(new ModelSelection(action.Model, action.ContextSize)))
            {
                throw new ArgumentException(
                    $"Model '{action.Model}' with context {action.ContextSize} is selected more than once.",
                    nameof(models));
            }
        }
    }

    private static void ValidateAgents(
        IReadOnlyList<AgentConfigurationAction> agents)
    {
        var kinds = new HashSet<AgentKind>();
        foreach (var action in agents)
        {
            ArgumentNullException.ThrowIfNull(action);
            ValidateActionId(action.ActionId);
            if (!Enum.IsDefined(action.AgentKind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(action.AgentKind),
                    action.AgentKind,
                    "Unknown agent kind.");
            }

            if (!Enum.IsDefined(action.Choice))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(action.Choice),
                    action.Choice,
                    "Unknown agent integration choice.");
            }

            var changesConfiguration =
                action.Choice != AgentIntegrationChoice.NoChange;
            if (action.Selected != changesConfiguration)
            {
                throw new InvalidOperationException(
                    $"Agent action '{action.ActionId}' has a contradictory selection and integration choice.");
            }

            ValidateConsent(action.Selected, action.ConsentGranted, action.ActionId);
            ValidateAgentPaths(action);

            if (!kinds.Add(action.AgentKind))
            {
                throw new ArgumentException(
                    $"Agent kind '{action.AgentKind}' is selected more than once.",
                    nameof(agents));
            }
        }
    }

    private static void ValidateAgentPaths(AgentConfigurationAction action)
    {
        switch (action.Choice)
        {
            case AgentIntegrationChoice.McpOnly:
                ValidateAbsolutePath(action.ConfigPath, nameof(action.ConfigPath));
                RequireAbsent(action.InstructionsPath, nameof(action.InstructionsPath));
                break;
            case AgentIntegrationChoice.InstructionsOnly:
                RequireAbsent(action.ConfigPath, nameof(action.ConfigPath));
                ValidateAbsolutePath(
                    action.InstructionsPath,
                    nameof(action.InstructionsPath));
                break;
            case AgentIntegrationChoice.McpAndInstructions:
                ValidateAbsolutePath(action.ConfigPath, nameof(action.ConfigPath));
                ValidateAbsolutePath(
                    action.InstructionsPath,
                    nameof(action.InstructionsPath));
                break;
            case AgentIntegrationChoice.NoChange:
                RequireAbsent(action.ConfigPath, nameof(action.ConfigPath));
                RequireAbsent(action.InstructionsPath, nameof(action.InstructionsPath));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action.Choice));
        }
    }

    private static void ValidateUniqueActionIds(
        IReadOnlyList<DependencyAction> dependencies,
        LocalAiPackageAction package,
        IReadOnlyList<ModelInstallAction> models,
        IReadOnlyList<AgentConfigurationAction> agents,
        IReadOnlyList<NonTransactionalEffect> effects)
    {
        var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var actionId in dependencies.Select(action => action.ActionId)
                     .Append(package.ActionId)
                     .Concat(models.Select(action => action.ActionId))
                     .Concat(agents.Select(action => action.ActionId))
                     .Concat(effects.Select(effect => effect.ActionId)))
        {
            ValidateActionId(actionId);
            if (!actionIds.Add(actionId))
            {
                throw new ArgumentException(
                    $"Action ID '{actionId}' occurs more than once.");
            }
        }
    }

    private static void ValidateEffects(
        IReadOnlyList<NonTransactionalEffect> effects,
        IReadOnlyList<DependencyAction> dependencies,
        IReadOnlyList<ModelInstallAction> models)
    {
        var externalActions = dependencies
            .Select(action => new ExternalAction(
                action.ActionId,
                action.Selected,
                action.ConsentGranted))
            .Concat(models.Select(action => new ExternalAction(
                action.ActionId,
                action.Selected,
                action.ConsentGranted)))
            .ToDictionary(action => action.ActionId, StringComparer.OrdinalIgnoreCase);

        foreach (var effect in effects)
        {
            ArgumentNullException.ThrowIfNull(effect);
            ValidateActionId(effect.ActionId);
            ValidateActionId(effect.RelatedActionId);
            ValidateRequiredText(effect.Description, nameof(effect.Description));

            if (!externalActions.TryGetValue(
                    effect.RelatedActionId,
                    out var relatedAction))
            {
                throw new ArgumentException(
                    $"Effect '{effect.ActionId}' does not refer to a dependency or model action.",
                    nameof(effects));
            }

            if (!relatedAction.Selected || !relatedAction.ConsentGranted)
            {
                throw new InvalidOperationException(
                    $"Effect '{effect.ActionId}' must refer to a selected and consented external action.");
            }
        }
    }

    private static void ValidateActionId(string actionId)
    {
        ValidateRequiredText(actionId, nameof(actionId));
        if (!IsIdentifier(actionId))
        {
            throw new ArgumentException(
                $"Action ID '{actionId}' is invalid.",
                nameof(actionId));
        }
    }

    private static bool IsIdentifier(string value)
    {
        if (!char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(
            character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-');
    }

    private static void ValidateRequiredToken(string value, string parameterName)
    {
        ValidateRequiredText(value, parameterName);
        if (value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} cannot contain whitespace or control characters.",
                parameterName);
        }
    }

    private static void ValidateRequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{parameterName} cannot be blank.",
                parameterName);
        }
    }

    private static void ValidateAbsolutePath(
        string? path,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                $"{parameterName} must be a fully qualified path.",
                parameterName);
        }
    }

    private static void RequireAbsent(string? value, string parameterName)
    {
        if (value is not null)
        {
            throw new ArgumentException(
                $"{parameterName} must be absent for this integration choice.",
                parameterName);
        }
    }

    private static void ValidateConsent(
        bool selected,
        bool consentGranted,
        string actionId)
    {
        if (selected && !consentGranted)
        {
            throw new InvalidOperationException(
                $"Selected action '{actionId}' requires explicit consent.");
        }
    }

    private static EnvironmentDiagnosis SnapshotDiagnosis(
        EnvironmentDiagnosis diagnosis) =>
        new(
            diagnosis.OperatingSystem with { },
            diagnosis.Disk with { },
            diagnosis.Network with { },
            diagnosis.WinGet with { },
            diagnosis.Git with { },
            diagnosis.Ollama with { },
            new GpuSnapshot(
                diagnosis.Gpu.State,
                diagnosis.Gpu.Adapters.Select(adapter => adapter with { }),
                diagnosis.Gpu.Reason),
            diagnosis.ExistingLocalAi with { },
            diagnosis.Agents.Select(
                agent => new AgentSnapshot(
                    agent.Kind,
                    agent.Executable with { },
                    agent.Config with { },
                    agent.Instructions with { })),
            diagnosis.UnsupportedReasons.ToArray());

    private readonly record struct ModelSelection(string Model, int ContextSize);

    private sealed class ModelSelectionComparer : IEqualityComparer<ModelSelection>
    {
        public static ModelSelectionComparer Instance { get; } = new();

        public bool Equals(ModelSelection x, ModelSelection y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Model, y.Model) &&
            x.ContextSize == y.ContextSize;

        public int GetHashCode(ModelSelection obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Model),
                obj.ContextSize);
    }

    private sealed record ExternalAction(
        string ActionId,
        bool Selected,
        bool ConsentGranted);
}
