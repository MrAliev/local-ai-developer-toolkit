using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed record ModelRoutingRequest(
    LocalTaskProfile Profile,
    int ContextTokens,
    string? ModelOverride,
    LocalWorkloadMetadata? Workload = null);

public sealed record ModelAvailability
{
    public ModelAvailability(
        IReadOnlyList<string> installedModels,
        IReadOnlyList<string> residentModels,
        IReadOnlyList<ModelContextRef> disabledContexts)
    {
        InstalledModels = new HashSet<string>(
            installedModels ?? throw new ArgumentNullException(nameof(installedModels)),
            StringComparer.Ordinal);
        ResidentModels = new HashSet<string>(
            residentModels ?? throw new ArgumentNullException(nameof(residentModels)),
            StringComparer.Ordinal);
        DisabledContexts = new HashSet<ModelContextRef>(
            disabledContexts ?? throw new ArgumentNullException(nameof(disabledContexts)));
    }

    public IReadOnlySet<string> InstalledModels { get; }

    public IReadOnlySet<string> ResidentModels { get; }

    public IReadOnlySet<ModelContextRef> DisabledContexts { get; }
}

public sealed record ModelSelection(
    LocalTaskProfile Profile,
    string Model,
    int ContextTokens,
    bool IsExperimentalAttempt,
    bool UsedFallback,
    string CatalogVersion,
    string Reason,
    LocalWorkloadMetadata? Workload = null);

/// <summary>
/// None of the models this task routes to is installed.
///
/// A distinct type because the type name is the failure code the broker records, and the code is
/// all that crosses back to the client -- messages do not, deliberately, since a raw exception
/// text is not something this boundary should hand out. "InvalidOperationException" told a caller
/// of read_image nothing at all; this one tells the tool layer enough to name what to install.
/// </summary>
public sealed class NoModelInstalledException(LocalTaskProfile profile)
    : InvalidOperationException(
        $"No model for task profile '{profile}' is installed.")
{
    public LocalTaskProfile Profile { get; } = profile;
}

/// <summary>
/// A model for this task is installed, but none can take this particular request -- the context
/// size asked for, or an image with more pixels than any of them accepts.
///
/// Separate from <see cref="NoModelInstalledException"/> because the two need opposite advice,
/// and only the type name survives the trip back to the client. Telling someone to install a
/// model they already have is worse than saying nothing.
/// </summary>
public sealed class NoEligibleModelException(LocalTaskProfile profile)
    : InvalidOperationException(
        $"No installed model is eligible for task profile '{profile}' with this request.")
{
    public LocalTaskProfile Profile { get; } = profile;
}

public sealed class ModelRouter(ModelRoutingCatalog catalog)
{
    private readonly ModelRoutingCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    public ModelSelection? Select(
        ModelRoutingRequest request,
        ModelAvailability availability,
        ExperimentSnapshot experiments)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(experiments);
        var route = _catalog.Route(request.Profile);
        if (route.Mode == LocalRouteMode.Deterministic)
        {
            if (request.ModelOverride is not null)
            {
                throw new InvalidOperationException(
                    $"Task profile '{request.Profile}' does not use a language model.");
            }

            return null;
        }

        if (request.ModelOverride is not null)
        {
            ValidateOverride(route, request, availability);
            var model = _catalog.Model(request.ModelOverride);
            return Selection(
                request,
                model.Tag,
                IsActiveExperiment(request.Profile, model, experiments),
                route.Fallbacks.Contains(model.Tag, StringComparer.Ordinal),
                "explicit model override");
        }

        var activeExperimental = route.Candidates
            .Select(_catalog.Model)
            .FirstOrDefault(model =>
                model.Lifecycle == LocalModelLifecycle.Experimental &&
                IsActiveExperiment(request.Profile, model, experiments) &&
                IsEligible(model, request, availability));
        if (activeExperimental is not null)
        {
            return Selection(
                request,
                activeExperimental.Tag,
                IsExperimentalAttempt: true,
                UsedFallback: false,
                "active per-profile experiment");
        }

        var promotedOrEstablished = route.Candidates
            .Select(_catalog.Model)
            .Where(model =>
                IsEligible(model, request, availability) &&
                (model.Lifecycle != LocalModelLifecycle.Experimental ||
                 experiments.Pair(request.Profile, model.Tag).IsPromoted))
            .OrderByDescending(model => availability.ResidentModels.Contains(model.Tag))
            .ThenBy(model => IndexOf(route.Candidates, model.Tag))
            .FirstOrDefault();
        if (promotedOrEstablished is not null)
        {
            return Selection(
                request,
                promotedOrEstablished.Tag,
                IsExperimentalAttempt: false,
                UsedFallback: false,
                availability.ResidentModels.Contains(promotedOrEstablished.Tag)
                    ? "suitable model is resident"
                    : "ordered catalog candidate");
        }

        var fallback = route.Fallbacks
            .Select(_catalog.Model)
            .FirstOrDefault(model =>
                IsEligible(model, request, availability));
        return fallback is null
            ? throw Unavailable(route, request.Profile, availability)
            : Selection(
                request,
                fallback.Tag,
                IsExperimentalAttempt: false,
                UsedFallback: true,
                "established fallback");
    }

    public ModelSelection SelectFallback(
        ModelSelection failedSelection,
        ModelExecutionOutcome outcome,
        ModelAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(failedSelection);
        ArgumentNullException.ThrowIfNull(availability);
        if (outcome == ModelExecutionOutcome.Success)
        {
            throw new ArgumentException(
                "A successful execution does not require fallback.",
                nameof(outcome));
        }

        var route = _catalog.Route(failedSelection.Profile);
        var fallback = route.Fallbacks
            .Where(tag => !string.Equals(
                tag,
                failedSelection.Model,
                StringComparison.Ordinal))
            .Select(_catalog.Model)
            .FirstOrDefault(model =>
                IsEligible(
                    model,
                    new ModelRoutingRequest(
                        failedSelection.Profile,
                        failedSelection.ContextTokens,
                        ModelOverride: null,
                        failedSelection.Workload),
                    availability))
            ?? throw Unavailable(
                _catalog.Route(failedSelection.Profile),
                failedSelection.Profile,
                availability);
        return failedSelection with
        {
            Model = fallback.Tag,
            IsExperimentalAttempt = false,
            UsedFallback = true,
            Reason = $"{outcome} fallback"
        };
    }

    /// <summary>
    /// Which of the two failures this is. "Install one of these" and "the ones you have cannot
    /// take this request" are opposite instructions, and the caller only receives the type name.
    /// </summary>
    private static InvalidOperationException Unavailable(
        TaskRouteEntry route,
        LocalTaskProfile profile,
        ModelAvailability availability) =>
        route.Candidates
            .Concat(route.Fallbacks)
            .Any(tag => availability.InstalledModels.Contains(tag))
            ? new NoEligibleModelException(profile)
            : new NoModelInstalledException(profile);

    private void ValidateOverride(
        TaskRouteEntry route,
        ModelRoutingRequest request,
        ModelAvailability availability)
    {
        var model = _catalog.Model(request.ModelOverride!);
        if (!route.Candidates
                .Concat(route.Fallbacks)
                .Contains(model.Tag, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Model '{model.Tag}' is not capable of task profile '{request.Profile}'.");
        }

        if (!IsEligible(model, request, availability))
        {
            throw new InvalidOperationException(
                $"Model '{model.Tag}' is not installed or eligible for " +
                $"{request.ContextTokens} context tokens.");
        }
    }

    private static bool IsActiveExperiment(
        LocalTaskProfile profile,
        ModelCatalogEntry model,
        ExperimentSnapshot experiments)
    {
        if (model.Lifecycle != LocalModelLifecycle.Experimental)
        {
            return false;
        }

        var pair = experiments.Pair(profile, model.Tag);
        return !pair.IsPromoted &&
               pair.OwnerAction is not (
                   ExperimentOwnerAction.FallbackOnly or
                   ExperimentOwnerAction.Disable) &&
               !pair.IsPaused &&
               !pair.IsCircuitOpen &&
               pair.CompletedAttempts < ExperimentPairState.BatchSize;
    }

    private static bool IsEligible(
        ModelCatalogEntry model,
        ModelRoutingRequest request,
        ModelAvailability availability) =>
        availability.InstalledModels.Contains(model.Tag) &&
        model.ContextTokens.Contains(request.ContextTokens) &&
        !availability.DisabledContexts.Contains(
            new ModelContextRef(model.Tag, request.ContextTokens)) &&
        (request.Workload is not { ImageCount: > 0 } workload ||
         model.SupportsImages &&
         model.MaxImagePixels is { } maxPixels &&
         workload.TotalImagePixels <= maxPixels) &&
        model.Lifecycle != LocalModelLifecycle.Disabled;

    private ModelSelection Selection(
        ModelRoutingRequest request,
        string model,
        bool IsExperimentalAttempt,
        bool UsedFallback,
        string reason) =>
        new(
            request.Profile,
            model,
            request.ContextTokens,
            IsExperimentalAttempt,
            UsedFallback,
            _catalog.CatalogVersion,
            reason,
            request.Workload);

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}

public sealed record ExperimentPairState(
    LocalTaskProfile Profile,
    string Model,
    int CompletedAttempts,
    int Successes,
    int TechnicalFailures,
    int StructuralFailures,
    int ContextFailures,
    int CpuOffloads,
    int ConsecutiveTechnicalFailures,
    bool IsPaused,
    bool IsCircuitOpen,
    bool IsPromoted,
    ExperimentOwnerAction? OwnerAction,
    IReadOnlyList<Guid>? CompletedWorkflows = null)
{
    public const int BatchSize = 10;

    public static ExperimentPairState Empty(
        LocalTaskProfile profile,
        string model) =>
        new(
            profile,
            model,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            false,
            false,
            null,
            []);
}

public sealed class ExperimentSnapshot
{
    private readonly IReadOnlyDictionary<ExperimentPairKey, ExperimentPairState> _pairs;

    private ExperimentSnapshot(
        IReadOnlyDictionary<ExperimentPairKey, ExperimentPairState> pairs)
    {
        _pairs = pairs;
    }

    public static ExperimentSnapshot Empty { get; } =
        new(new Dictionary<ExperimentPairKey, ExperimentPairState>());

    public IReadOnlyCollection<ExperimentPairState> Pairs =>
        Array.AsReadOnly(_pairs.Values.ToArray());

    public ExperimentPairState Pair(LocalTaskProfile profile, string model) =>
        _pairs.TryGetValue(new ExperimentPairKey(profile, model), out var pair)
            ? pair
            : ExperimentPairState.Empty(profile, model);

    public ExperimentSnapshot Record(
        LocalTaskProfile profile,
        string model,
        ModelExecutionOutcome outcome)
    {
        var current = Pair(profile, model);
        var completed = current.CompletedAttempts + 1;
        var consecutiveTechnicalFailures =
            outcome == ModelExecutionOutcome.TechnicalFailure
                ? current.ConsecutiveTechnicalFailures + 1
                : 0;
        var updated = current with
        {
            CompletedAttempts = completed,
            Successes = current.Successes +
                        (outcome == ModelExecutionOutcome.Success ? 1 : 0),
            TechnicalFailures = current.TechnicalFailures +
                                (outcome == ModelExecutionOutcome.TechnicalFailure ? 1 : 0),
            StructuralFailures = current.StructuralFailures +
                                 (outcome == ModelExecutionOutcome.StructuralFailure ? 1 : 0),
            ContextFailures = current.ContextFailures +
                              (outcome == ModelExecutionOutcome.ContextFailure ? 1 : 0),
            CpuOffloads = current.CpuOffloads +
                          (outcome == ModelExecutionOutcome.CpuOffload ? 1 : 0),
            ConsecutiveTechnicalFailures = consecutiveTechnicalFailures,
            IsPaused = completed >= ExperimentPairState.BatchSize,
            IsCircuitOpen = consecutiveTechnicalFailures >= 2
        };
        return With(updated);
    }

    public ExperimentSnapshot RecordWorkflow(
        Guid workflowId,
        LocalTaskProfile profile,
        string model,
        ModelExecutionOutcome outcome)
    {
        if (workflowId == Guid.Empty)
        {
            throw new ArgumentException(
                "Workflow ID cannot be empty.",
                nameof(workflowId));
        }

        var current = Pair(profile, model);
        if ((current.CompletedWorkflows ?? []).Contains(workflowId))
        {
            return this;
        }

        var recorded = Record(profile, model, outcome);
        var updated = recorded.Pair(profile, model) with
        {
            CompletedWorkflows = Array.AsReadOnly(
                [.. current.CompletedWorkflows ?? [], workflowId])
        };
        return recorded.With(updated);
    }

    public ExperimentSnapshot ApplyFeedback(
        LocalTaskProfile profile,
        string model,
        ExperimentOwnerAction action)
    {
        var current = Pair(profile, model);
        var updated = action switch
        {
            ExperimentOwnerAction.Promote => current with
            {
                OwnerAction = action,
                IsPromoted = true,
                IsPaused = false,
                IsCircuitOpen = false,
                ConsecutiveTechnicalFailures = 0
            },
            ExperimentOwnerAction.ContinueExperiment => current with
            {
                OwnerAction = action,
                CompletedAttempts = 0,
                Successes = 0,
                TechnicalFailures = 0,
                StructuralFailures = 0,
                ContextFailures = 0,
                CpuOffloads = 0,
                IsPaused = false,
                IsCircuitOpen = false,
                ConsecutiveTechnicalFailures = 0,
                CompletedWorkflows = []
            },
            ExperimentOwnerAction.FallbackOnly => current with
            {
                OwnerAction = action,
                IsPaused = true,
                IsPromoted = false
            },
            ExperimentOwnerAction.Disable => current with
            {
                OwnerAction = action,
                IsPaused = true,
                IsPromoted = false,
                IsCircuitOpen = true
            },
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return With(updated);
    }

    public static ExperimentSnapshot FromPairs(
        IEnumerable<ExperimentPairState> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var dictionary = new Dictionary<ExperimentPairKey, ExperimentPairState>();
        foreach (var pair in pairs)
        {
            var key = new ExperimentPairKey(pair.Profile, pair.Model);
            if (!dictionary.TryAdd(key, pair))
            {
                throw new InvalidDataException(
                    $"Duplicate experiment state for '{pair.Profile}/{pair.Model}'.");
            }
        }

        return new ExperimentSnapshot(dictionary);
    }

    private ExperimentSnapshot With(ExperimentPairState pair)
    {
        var copy = new Dictionary<ExperimentPairKey, ExperimentPairState>(_pairs)
        {
            [new ExperimentPairKey(pair.Profile, pair.Model)] = pair
        };
        return new ExperimentSnapshot(copy);
    }

    private sealed record ExperimentPairKey(LocalTaskProfile Profile, string Model);
}
