using System.Text;
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

        ValidateEffectElements(effectSnapshot);
        ValidateDependencies(dependencySnapshot);
        var packageSnapshot = ValidatePackage(package);
        ValidateModels(modelSnapshot);
        agentSnapshot = ValidateAgents(agentSnapshot);
        ValidateUniqueActionIds(
            dependencySnapshot,
            packageSnapshot,
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
            packageSnapshot,
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
            ValidateVersionToken(action.Version, nameof(action.Version));
            ValidateConsent(action.Selected, action.ConsentGranted, action.ActionId);

            if (!packageIds.Add(action.PackageId))
            {
                throw new ArgumentException(
                    $"Dependency package ID '{action.PackageId}' is selected more than once.",
                    nameof(dependencies));
            }
        }
    }

    private static LocalAiPackageAction ValidatePackage(
        LocalAiPackageAction package)
    {
        ValidateActionId(package.ActionId);
        ValidateVersionToken(package.Version, nameof(package.Version));
        ValidateConsent(
            package.Selected,
            package.ConsentGranted,
            package.ActionId);
        return package with
        {
            PackagePath = CanonicalizeWindowsPath(
                package.PackagePath,
                nameof(package.PackagePath)),
        };
    }

    private static void ValidateModels(
        IReadOnlyList<ModelInstallAction> models)
    {
        var modelsByIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in models)
        {
            ArgumentNullException.ThrowIfNull(action);
            ValidateActionId(action.ActionId);
            ValidateModelReference(action.Model, nameof(action.Model));
            if (action.ContextSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(action.ContextSize),
                    action.ContextSize,
                    "Model context size must be positive.");
            }

            ValidateConsent(action.Selected, action.ConsentGranted, action.ActionId);
            if (!modelsByIdentity.Add(action.Model))
            {
                throw new ArgumentException(
                    $"Model '{action.Model}' is selected more than once.",
                    nameof(models));
            }
        }
    }

    private static void ValidateEffectElements(
        IReadOnlyList<NonTransactionalEffect> effects)
    {
        for (var index = 0; index < effects.Count; index++)
        {
            if (effects[index] is null)
            {
                throw new ArgumentException(
                    $"Effects cannot contain a null element at index {index}.",
                    nameof(effects));
            }
        }
    }

    private static AgentConfigurationAction[] ValidateAgents(
        IReadOnlyList<AgentConfigurationAction> agents)
    {
        var kinds = new HashSet<AgentKind>();
        var validatedAgents = new AgentConfigurationAction[agents.Count];
        for (var index = 0; index < agents.Count; index++)
        {
            var action = agents[index];
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
            validatedAgents[index] = CanonicalizeAgentPaths(action);

            if (!kinds.Add(action.AgentKind))
            {
                throw new ArgumentException(
                    $"Agent kind '{action.AgentKind}' is selected more than once.",
                    nameof(agents));
            }
        }

        return validatedAgents;
    }

    private static AgentConfigurationAction CanonicalizeAgentPaths(
        AgentConfigurationAction action)
    {
        switch (action.Choice)
        {
            case AgentIntegrationChoice.McpOnly:
                RequireAbsent(action.InstructionsPath, nameof(action.InstructionsPath));
                return action with
                {
                    ConfigPath = CanonicalizeWindowsPath(
                        action.ConfigPath,
                        nameof(action.ConfigPath)),
                };
            case AgentIntegrationChoice.InstructionsOnly:
                RequireAbsent(action.ConfigPath, nameof(action.ConfigPath));
                return action with
                {
                    InstructionsPath = CanonicalizeWindowsPath(
                        action.InstructionsPath,
                        nameof(action.InstructionsPath)),
                };
            case AgentIntegrationChoice.McpAndInstructions:
                return action with
                {
                    ConfigPath = CanonicalizeWindowsPath(
                        action.ConfigPath,
                        nameof(action.ConfigPath)),
                    InstructionsPath = CanonicalizeWindowsPath(
                        action.InstructionsPath,
                        nameof(action.InstructionsPath)),
                };
            case AgentIntegrationChoice.NoChange:
                RequireAbsent(action.ConfigPath, nameof(action.ConfigPath));
                RequireAbsent(action.InstructionsPath, nameof(action.InstructionsPath));
                return action with { };
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
        var semanticEffects = new HashSet<EffectSemanticKey>(
            EffectSemanticKeyComparer.Instance);

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

            var semanticKey = new EffectSemanticKey(
                effect.RelatedActionId,
                NormalizeWhitespace(effect.Description));
            if (!semanticEffects.Add(semanticKey))
            {
                throw new ArgumentException(
                    $"Effect '{effect.ActionId}' duplicates an existing effect for action '{effect.RelatedActionId}'.",
                    nameof(effects));
            }
        }
    }

    private static string NormalizeWhitespace(string value)
    {
        var normalized = new StringBuilder(value.Length);
        var needsSeparator = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                needsSeparator = normalized.Length > 0;
                continue;
            }

            if (needsSeparator)
            {
                normalized.Append(' ');
                needsSeparator = false;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
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

    private static void ValidateVersionToken(
        string value,
        string parameterName)
    {
        ValidateRequiredText(value, parameterName);
        if (!IsVersionToken(value))
        {
            throw new ArgumentException(
                $"{parameterName} must be a 1-128 character immutable version token.",
                parameterName);
        }
    }

    private static void ValidateModelReference(
        string value,
        string parameterName)
    {
        ValidateRequiredText(value, parameterName);
        if (value.Length > 128)
        {
            throw new ArgumentException(
                $"{parameterName} must be at most 128 characters.",
                parameterName);
        }

        var separator = value.IndexOf(':');
        if (separator < 0)
        {
            ValidateVersionToken(value, parameterName);
            return;
        }

        if (separator != value.LastIndexOf(':') ||
            !IsVersionToken(value[..separator]) ||
            !IsVersionToken(value[(separator + 1)..]))
        {
            throw new ArgumentException(
                $"{parameterName} must be an immutable model token with at most one tag separator.",
                parameterName);
        }
    }

    // Grammar: 1-128 ASCII characters; an alphanumeric first and last
    // character; inner alphanumerics, '.', '-', or '_'; never '..'.
    private static bool IsVersionToken(string value)
    {
        if (value.Length is < 1 or > 128 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            !char.IsAsciiLetterOrDigit(value[^1]) ||
            value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return value.All(
            character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_');
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

    private static string CanonicalizeWindowsPath(
        string? path,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length < 4 ||
            !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' ||
            path[2] != '\\' ||
            path.Contains('/'))
        {
            throw new ArgumentException(
                $"{parameterName} must be an absolute drive-qualified Windows path.",
                parameterName);
        }

        var segments = path[3..].Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                segment.Any(IsInvalidWindowsFileNameCharacter) ||
                IsReservedDosDeviceSegment(segment))
            {
                throw new ArgumentException(
                    $"{parameterName} contains an invalid or ambiguous Windows path segment.",
                    parameterName);
            }
        }

        var canonicalPath =
            $"{char.ToUpperInvariant(path[0])}:\\{string.Join('\\', segments)}";
        var fullPath = Path.GetFullPath(canonicalPath);
        if (!StringComparer.Ordinal.Equals(canonicalPath, fullPath))
        {
            throw new ArgumentException(
                $"{parameterName} contains path normalization ambiguity.",
                parameterName);
        }

        return fullPath;
    }

    private static bool IsInvalidWindowsFileNameCharacter(char character) =>
        char.IsControl(character) ||
        character is '<' or '>' or ':' or '"' or '|' or '?' or '*';

    private static bool IsReservedDosDeviceSegment(string segment)
    {
        var extensionSeparator = segment.IndexOf('.');
        var baseName = (extensionSeparator < 0
                ? segment
                : segment[..extensionSeparator])
            .TrimEnd(' ', '.');

        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return baseName.Length == 4 &&
               IsReservedPortNumber(baseName[3]) &&
               (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReservedPortNumber(char character) =>
        character is >= '1' and <= '9' or
            '\u00B9' or // superscript one
            '\u00B2' or // superscript two
            '\u00B3';   // superscript three

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
        if (selected != consentGranted)
        {
            throw new InvalidOperationException(
                selected
                    ? $"Selected action '{actionId}' requires explicit consent."
                    : $"Unselected action '{actionId}' cannot retain consent.");
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
            diagnosis.GitHubCli with { },
            diagnosis.GitHubSignIn with { },
            diagnosis.Ollama with { },
            diagnosis.DotNetSdk with { },
            diagnosis.NodeJs with { },
            diagnosis.Npm with { },
            diagnosis.ScipTypeScript with { },
            diagnosis.Python with { },
            diagnosis.ScipPython with { },
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

    private sealed record ExternalAction(
        string ActionId,
        bool Selected,
        bool ConsentGranted);

    private readonly record struct EffectSemanticKey(
        string RelatedActionId,
        string Description);

    private sealed class EffectSemanticKeyComparer :
        IEqualityComparer<EffectSemanticKey>
    {
        public static EffectSemanticKeyComparer Instance { get; } = new();

        public bool Equals(EffectSemanticKey x, EffectSemanticKey y) =>
            StringComparer.OrdinalIgnoreCase.Equals(
                x.RelatedActionId,
                y.RelatedActionId) &&
            StringComparer.OrdinalIgnoreCase.Equals(
                x.Description,
                y.Description);

        public int GetHashCode(EffectSemanticKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    obj.RelatedActionId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Description));
    }
}
