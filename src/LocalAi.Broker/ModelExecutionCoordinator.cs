using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed record ModelValidationResult(
    bool Passed,
    ModelExecutionOutcome Outcome,
    string Code)
{
    public static ModelValidationResult Pass(string code) =>
        new(true, ModelExecutionOutcome.Success, code);

    public static ModelValidationResult Fail(
        ModelExecutionOutcome outcome,
        string code)
    {
        if (outcome == ModelExecutionOutcome.Success)
        {
            throw new ArgumentException(
                "Failed validation requires a failure outcome.",
                nameof(outcome));
        }

        return new ModelValidationResult(false, outcome, code);
    }
}

public sealed class ModelExecutionCoordinator
{
    private static readonly TimeSpan WarmProofLifetime = TimeSpan.FromMinutes(1);

    private readonly ModelRouter _router;
    private readonly IModelRuntime _runtime;
    private readonly ExperimentStateStore _experiments;
    private readonly ModelTelemetryStore _telemetry;
    private readonly Func<LocalJobRequest, CancellationToken, Task<BrokerExecutionResult>> _execute;
    private readonly Func<ModelSelection, BrokerExecutionResult, ModelValidationResult> _validate;
    private readonly TimeProvider _timeProvider;
    private ModelResidencyProof? _warmProof;

    public ModelExecutionCoordinator(
        ModelRouter router,
        IModelRuntime runtime,
        ExperimentStateStore experiments,
        ModelTelemetryStore telemetry,
        Func<LocalJobRequest, CancellationToken, Task<BrokerExecutionResult>> execute,
        Func<ModelSelection, BrokerExecutionResult, ModelValidationResult>? validate = null,
        TimeProvider? timeProvider = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _experiments = experiments ?? throw new ArgumentNullException(nameof(experiments));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _validate = validate ?? ((_, _) => ModelValidationResult.Pass("none"));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BrokerExecutionResult> ExecuteAsync(
        LocalJobRequest request,
        ModelAvailability availability,
        CancellationToken cancellationToken = default)
    {
        var selection = await SelectAsync(
            request,
            availability,
            cancellationToken);
        return await ExecuteAsync(
            request,
            availability,
            selection,
            cancellationToken);
    }

    public async Task<ModelSelection> SelectAsync(
        LocalJobRequest request,
        ModelAvailability availability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(availability);
        var chat = request.Payload as ChatJobPayload
            ?? throw new ArgumentException(
                "Model execution coordinator currently accepts routed chat jobs.",
                nameof(request));
        var profile = chat.TaskProfile
            ?? throw new ArgumentException("Chat job is not routed.", nameof(request));
        var workload = chat.Workload
            ?? throw new ArgumentException(
                "Routed chat has no workload metadata.",
                nameof(request));
        var context = chat.RequestedContextTokens ?? 2048;
        var experiments = await _experiments.LoadAsync(cancellationToken);
        return _router.Select(
            new ModelRoutingRequest(
                profile,
                context,
                chat.Model,
                workload),
            availability,
            experiments)
            ?? throw new InvalidOperationException(
                $"Task profile '{profile}' is deterministic.");
    }

    public async Task<BrokerExecutionResult> ExecuteAsync(
        LocalJobRequest request,
        ModelAvailability availability,
        ModelSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(selection);
        var chat = request.Payload as ChatJobPayload
            ?? throw new ArgumentException(
                "Model execution coordinator currently accepts routed chat jobs.",
                nameof(request));
        var profile = chat.TaskProfile
            ?? throw new ArgumentException("Chat job is not routed.", nameof(request));
        var workload = chat.Workload
            ?? throw new ArgumentException(
                "Routed chat has no workload metadata.",
                nameof(request));
        var context = chat.RequestedContextTokens ?? 2048;
        if (selection.Profile != profile ||
            selection.ContextTokens != context ||
            selection.Workload != workload)
        {
            throw new ArgumentException(
                "Prepared model selection does not match the routed request.",
                nameof(selection));
        }

        var workflowOwnsExperimentOutcome = chat.Workflow is not null;
        var experiments = await _experiments.LoadAsync(cancellationToken);
        AttemptResult primary;
        try
        {
            primary = await ExecuteAttemptAsync(
                request,
                selection,
                workload,
                cancellationToken);
        }
        catch (ModelPreflightException preflight)
        {
            if (!workflowOwnsExperimentOutcome)
            {
                experiments = RecordIfExperimental(
                    experiments,
                    selection,
                    preflight.Outcome);
                await _experiments.SaveAsync(experiments, cancellationToken);
            }

            ModelSelection preflightFallback;
            try
            {
                preflightFallback = _router.SelectFallback(
                    selection,
                    preflight.Outcome,
                    availability);
            }
            catch (InvalidOperationException)
            {
                throw preflight;
            }

            var recovered = await ExecuteAttemptAsync(
                request,
                preflightFallback,
                workload,
                cancellationToken);
            return AnnotateExperimentalFailure(
                recovered.Result,
                selection,
                preflight.Outcome);
        }
        catch (ModelAttemptExecutionException)
        {
            const ModelExecutionOutcome outcome =
                ModelExecutionOutcome.TechnicalFailure;
            if (!workflowOwnsExperimentOutcome)
            {
                experiments = RecordIfExperimental(
                    experiments,
                    selection,
                    outcome);
                await _experiments.SaveAsync(experiments, cancellationToken);
            }

            var technicalFallback = _router.SelectFallback(
                selection,
                outcome,
                availability);
            var recovered = await ExecuteAttemptAsync(
                request,
                technicalFallback,
                workload,
                cancellationToken);
            return AnnotateExperimentalFailure(
                recovered.Result,
                selection,
                outcome);
        }

        if (!workflowOwnsExperimentOutcome)
        {
            experiments = RecordIfExperimental(
                experiments,
                selection,
                primary.Validation.Outcome);
            await _experiments.SaveAsync(experiments, cancellationToken);
        }

        if (primary.Validation.Passed)
        {
            return primary.Result;
        }

        var validationFallback = _router.SelectFallback(
            selection,
            primary.Validation.Outcome,
            availability);
        var fallbackAttempt = await ExecuteAttemptAsync(
            request,
            validationFallback,
            workload,
            cancellationToken);
        return AnnotateExperimentalFailure(
            fallbackAttempt.Result,
            selection,
            primary.Validation.Outcome);
    }

    private async Task<AttemptResult> ExecuteAttemptAsync(
        LocalJobRequest original,
        ModelSelection selection,
        LocalWorkloadMetadata workload,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var wasCold = _warmProof is null ||
                      !string.Equals(
                          _warmProof.Model,
                          selection.Model,
                          StringComparison.Ordinal) ||
                      _warmProof.ContextTokens != selection.ContextTokens ||
                      now - _warmProof.VerifiedAtUtc > WarmProofLifetime;
        var loadStarted = now;
        if (wasCold)
        {
            _warmProof = await _runtime.EnsureReadyAsync(
                selection.Model,
                selection.ContextTokens,
                cancellationToken);
        }

        var loadCompleted = _timeProvider.GetUtcNow();
        var resolved = LocalJobRequestFactory.ResolveRoutedChat(
            original,
            selection.Model);
        var executionStarted = _timeProvider.GetUtcNow();
        var gross = Math.Max(0, (workload.InputCharacters + 3L) / 4L);
        const long verification = 0;
        var net = Math.Max(0, gross - verification);
        BrokerExecutionResult raw;
        try
        {
            raw = await _execute(resolved, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception executionException)
        {
            var failedAt = _timeProvider.GetUtcNow();
            try
            {
                await AppendTelemetryAsync(
                    original,
                    selection,
                    workload,
                    wasCold,
                    "execution:fail",
                    ModelExecutionOutcome.TechnicalFailure,
                    loadStarted,
                    loadCompleted,
                    executionStarted,
                    failedAt,
                    gross,
                    verification,
                    net,
                    cancellationToken);
            }
            catch (Exception telemetryException)
            {
                throw new ModelAttemptExecutionException(
                    new AggregateException(
                        executionException,
                        telemetryException));
            }

            throw new ModelAttemptExecutionException(executionException);
        }

        var executionCompleted = _timeProvider.GetUtcNow();
        var validation = _validate(selection, raw);
        var routing = new LocalRoutingReceipt(
            selection.Profile,
            selection.Model,
            selection.ContextTokens,
            wasCold,
            selection.UsedFallback,
            $"{validation.Code}:{(validation.Passed ? "pass" : "fail")}",
            gross,
            verification,
            net,
            selection.IsExperimentalAttempt);
        var result = raw with { Routing = routing };
        await AppendTelemetryAsync(
            original,
            selection,
            workload,
            wasCold,
            routing.ValidatorResult!,
            validation.Outcome,
            loadStarted,
            loadCompleted,
            executionStarted,
            executionCompleted,
            gross,
            verification,
            net,
            cancellationToken);
        return new AttemptResult(result, validation);
    }

    private async Task AppendTelemetryAsync(
        LocalJobRequest original,
        ModelSelection selection,
        LocalWorkloadMetadata workload,
        bool wasCold,
        string validatorResult,
        ModelExecutionOutcome outcome,
        DateTimeOffset loadStarted,
        DateTimeOffset loadCompleted,
        DateTimeOffset executionStarted,
        DateTimeOffset executionCompleted,
        long gross,
        long verification,
        long net,
        CancellationToken cancellationToken)
    {
        await _telemetry.AppendAsync(
            new ModelTelemetryRecord(
                original.JobId,
                selection.Profile,
                selection.Model,
                selection.ContextTokens,
                Bucket(workload.InputCharacters),
                Bucket(workload.ExpectedOutputCharacters),
                wasCold,
                ModelSwitched: wasCold,
                selection.UsedFallback,
                validatorResult,
                outcome,
                NonNegative(loadStarted - original.CreatedAtUtc),
                NonNegative(loadCompleted - loadStarted),
                NonNegative(executionCompleted - executionStarted),
                NonNegative(executionCompleted - original.CreatedAtUtc),
                gross,
                verification,
                net,
                selection.CatalogVersion,
                executionCompleted),
            cancellationToken);
    }

    private static BrokerExecutionResult AnnotateExperimentalFailure(
        BrokerExecutionResult result,
        ModelSelection failedSelection,
        ModelExecutionOutcome outcome) =>
        failedSelection.IsExperimentalAttempt && result.Routing is { } routing
            ? result with
            {
                Routing = routing with
                {
                    ExperimentalModel = failedSelection.Model,
                    ExperimentalOutcome = outcome
                }
            }
            : result;

    private static ExperimentSnapshot RecordIfExperimental(
        ExperimentSnapshot state,
        ModelSelection selection,
        ModelExecutionOutcome outcome) =>
        selection.IsExperimentalAttempt
            ? state.Record(selection.Profile, selection.Model, outcome)
            : state;

    private static LocalSizeBucket Bucket(int characters) =>
        characters switch
        {
            0 => LocalSizeBucket.Empty,
            <= 4_000 => LocalSizeBucket.Small,
            <= 16_000 => LocalSizeBucket.Medium,
            _ => LocalSizeBucket.Large
        };

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private sealed record AttemptResult(
        BrokerExecutionResult Result,
        ModelValidationResult Validation);

    private sealed class ModelAttemptExecutionException(Exception innerException)
        : Exception("Model execution failed.", innerException);
}
