using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class ModelControlService(
    ModelRoutingCatalog catalog,
    IModelRuntimeTransport transport,
    ExperimentStateStore experiments,
    ModelTelemetryStore telemetry,
    IModelRuntime? runtime = null,
    ISelectableBrokerQueue? queue = null)
{
    private readonly ModelRoutingCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IModelRuntimeTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly ExperimentStateStore _experiments =
        experiments ?? throw new ArgumentNullException(nameof(experiments));
    private readonly ModelTelemetryStore _telemetry =
        telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    private readonly IModelRuntime? _runtime = runtime;
    private readonly ISelectableBrokerQueue? _queue = queue;

    public async Task<LocalModelsStatusOutput> StatusAsync(
        CancellationToken cancellationToken = default)
    {
        var installed = await _transport.ListInstalledAsync(cancellationToken);
        var processes = await _transport.ListProcessesAsync(cancellationToken);
        var state = await _experiments.LoadAsync(cancellationToken);
        var queued = _queue is null
            ? []
            : await _queue.ListQueuedAsync(cancellationToken);
        var missing = _catalog.MaintenanceAllowlist
            .Except(installed, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new LocalModelsStatusOutput(
            Array.AsReadOnly(installed.Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(
                processes
                    .Select(process => process.Model)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()),
            Array.AsReadOnly(missing),
            Array.AsReadOnly(
                state.Pairs
                    .OrderBy(pair => pair.Profile)
                    .ThenBy(pair => pair.Model, StringComparer.Ordinal)
                    .Select(pair => new LocalExperimentPairStatus(
                        pair.Profile,
                        pair.Model,
                        pair.CompletedAttempts,
                        pair.IsPaused,
                        pair.IsCircuitOpen,
                        pair.IsPromoted,
                        pair.OwnerAction))
                    .ToArray()),
            _catalog.CatalogVersion,
            Array.AsReadOnly(
                processes
                    .OrderBy(process => process.Model, StringComparer.Ordinal)
                    .Select(process => new LocalModelResidencyStatus(
                        process.Model,
                        process.ContextTokens,
                        process.SizeBytes,
                        process.SizeVramBytes,
                        process.SizeBytes > 0 &&
                        process.SizeVramBytes == process.SizeBytes,
                        process.ExpiresAtUtc))
                    .ToArray()),
            Array.AsReadOnly(
                _runtime is null
                    ? []
                    : _catalog.Models
                        .SelectMany(model => model.ContextTokens.Select(
                            context => new ModelContextRef(model.Tag, context)))
                        .Where(context => _runtime.IsDisabled(
                            context.Model,
                            context.ContextTokens))
                        .OrderBy(context => context.Model, StringComparer.Ordinal)
                        .ThenBy(context => context.ContextTokens)
                        .ToArray()),
            Array.AsReadOnly(
                queued
                    .Select(candidate => candidate.Request.Payload)
                    .OfType<ModelMaintenanceJobPayload>()
                    .Where(payload =>
                        payload.Operation == ModelMaintenanceOperation.Pull)
                    .Select(payload => payload.Model)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()));
    }

    public async Task<ExperimentPairState> ApplyFeedbackAsync(
        LocalTaskProfile profile,
        string model,
        ExperimentOwnerAction action,
        CancellationToken cancellationToken = default)
    {
        _catalog.Model(model);
        var state = await _experiments.LoadAsync(cancellationToken);
        var current = state.Pair(profile, model);
        var resetsOpenCircuit =
            action == ExperimentOwnerAction.ContinueExperiment &&
            current.IsCircuitOpen;
        if (!current.IsPaused && !resetsOpenCircuit)
        {
            throw new InvalidOperationException(
                $"Feedback for '{profile}/{model}' is available only after " +
                $"{ExperimentPairState.BatchSize} completed experiment tasks " +
                "and the report gate has paused the pair.");
        }

        var updated = state.ApplyFeedback(profile, model, action);
        await _experiments.SaveAsync(updated, cancellationToken);
        return updated.Pair(profile, model);
    }

    public async Task<LocalExperimentCompletionOutput> CompleteExperimentAsync(
        Guid workflowId,
        LocalTaskProfile profile,
        string model,
        ModelExecutionOutcome outcome,
        LocalExperimentTaskMetrics metrics,
        CancellationToken cancellationToken = default)
    {
        if (workflowId == Guid.Empty)
        {
            throw new ArgumentException(
                "Workflow ID cannot be empty.",
                nameof(workflowId));
        }

        _catalog.Model(model);
        ArgumentNullException.ThrowIfNull(metrics);
        if (metrics.InputTokens < 0 ||
            metrics.OutputTokens < 0 ||
            metrics.LocalTokensProcessed < 0 ||
            metrics.EstimatedCloudGenerationTokensSaved < 0 ||
            metrics.EstimatedNetCloudContextTokensSaved < 0 ||
            metrics.TotalDuration < TimeSpan.Zero ||
            metrics.ColdExecutions < 0 ||
            metrics.WarmExecutions < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(metrics),
                "Experiment task metrics cannot be negative.");
        }

        await _telemetry.AppendExperimentTaskAsync(
            new ExperimentTaskTelemetryRecord(
                workflowId,
                profile,
                model,
                outcome,
                metrics.TotalDuration,
                metrics.ColdExecutions,
                metrics.WarmExecutions,
                metrics.UsedFallback,
                metrics.InputTokens,
                metrics.OutputTokens,
                metrics.LocalTokensProcessed,
                metrics.EstimatedCloudGenerationTokensSaved,
                metrics.EstimatedNetCloudContextTokensSaved,
                _catalog.CatalogVersion,
                DateTimeOffset.UtcNow),
            cancellationToken);
        var state = await _experiments.LoadAsync(cancellationToken);
        var updated = state.RecordWorkflow(
            workflowId,
            profile,
            model,
            outcome);
        await _experiments.SaveAsync(updated, cancellationToken);
        return new LocalExperimentCompletionOutput(
            workflowId,
            profile,
            model,
            outcome);
    }

    public async Task<LocalExperimentReportOutput> ReportAsync(
        LocalTaskProfile profile,
        string model,
        CancellationToken cancellationToken = default)
    {
        var state = await _experiments.LoadAsync(cancellationToken);
        var currentWorkflows = (state.Pair(profile, model).CompletedWorkflows ?? [])
            .ToHashSet();
        var records = (await _telemetry.ReadExperimentTasksAsync(cancellationToken))
            .Where(record =>
                currentWorkflows.Contains(record.WorkflowId) &&
                record.TaskProfile == profile &&
                string.Equals(record.Model, model, StringComparison.Ordinal))
            .ToArray();
        var durations = records
            .Select(record => record.TotalDuration)
            .Order()
            .ToArray();
        var mean = durations.Length == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)durations.Average(value => value.Ticks));
        var median = Percentile(durations, 0.50);
        var p90 = Percentile(durations, 0.90);
        return new LocalExperimentReportOutput(
            profile,
            model,
            records.Length,
            records.Count(record => record.Outcome == ModelExecutionOutcome.Success),
            records.Count(record => record.Outcome != ModelExecutionOutcome.Success),
            records.Count(record => record.UsedFallback),
            mean,
            median,
            p90,
            records.Sum(record => record.ColdExecutions),
            records.Sum(record => record.WarmExecutions),
            records.Sum(record => (long)record.EstimatedNetCloudContextTokensSaved),
            records.Sum(record => (long)record.LocalTokensProcessed),
            records.Sum(record => (long)record.EstimatedCloudGenerationTokensSaved),
            records.Sum(record => (long)record.EstimatedNetCloudContextTokensSaved));
    }

    private static TimeSpan Percentile(TimeSpan[] ordered, double percentile)
    {
        if (ordered.Length == 0)
        {
            return TimeSpan.Zero;
        }

        var index = Math.Max(0, (int)Math.Ceiling(ordered.Length * percentile) - 1);
        return ordered[index];
    }
}
