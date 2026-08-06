using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class BrokerExecutionRouter
{
    private readonly ModelRoutingCatalog _catalog;
    private readonly IModelRuntimeTransport _transport;
    private readonly IModelRuntime _runtime;
    private readonly ModelExecutionCoordinator _coordinator;
    private readonly ModelControlService _control;
    private readonly Func<
        LocalJobRequest,
        CancellationToken,
        Task<BrokerExecutionResult>> _executeDirect;
    private readonly Dictionary<Guid, PreparedModelExecution> _prepared = [];
    private string? _residentModel;

    public BrokerExecutionRouter(
        ModelRoutingCatalog catalog,
        IModelRuntimeTransport transport,
        IModelRuntime runtime,
        ModelExecutionCoordinator coordinator,
        ModelControlService control,
        Func<
            LocalJobRequest,
            CancellationToken,
            Task<BrokerExecutionResult>> executeDirect)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _executeDirect = executeDirect
            ?? throw new ArgumentNullException(nameof(executeDirect));
    }

    public string? ResidentModel => Volatile.Read(ref _residentModel);

    public async Task<IReadOnlyDictionary<Guid, ModelSelection>> PrepareAsync(
        IReadOnlyList<QueuedJobCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _prepared.Clear();
        var routed = candidates
            .Where(candidate =>
                candidate.Request.Payload is ChatJobPayload
                {
                    TaskProfile: not null
                })
            .ToArray();
        if (routed.Length == 0)
        {
            return new Dictionary<Guid, ModelSelection>();
        }

        var availability = await AvailabilityAsync(cancellationToken);
        foreach (var candidate in routed)
        {
            try
            {
                var selection = await _coordinator.SelectAsync(
                    candidate.Request,
                    availability,
                    cancellationToken);
                _prepared[candidate.Request.JobId] =
                    new PreparedModelExecution(selection, availability);
            }
            catch (InvalidOperationException)
            {
                // A permanently invalid candidate must not prevent valid queued
                // work from being scheduled. Its execution will repeat selection
                // and persist the terminal failure for that job alone.
            }
        }

        return _prepared.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Selection);
    }

    public async Task UnloadResidentAsync(
        CancellationToken cancellationToken = default)
    {
        var processes = await _transport.ListProcessesAsync(cancellationToken);
        var managedModels = _catalog.Models
            .Select(model => model.Tag)
            .ToHashSet(StringComparer.Ordinal);
        var models = processes
            .Select(process => process.Model)
            .Append(ResidentModel)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Where(model => managedModels.Contains(model!))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
        foreach (var model in models)
        {
            await _transport.UnloadAsync(model, cancellationToken);
        }

        Volatile.Write(ref _residentModel, null);
    }

    public Task<BrokerExecutionResult> ExecuteAsync(
        LocalJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Payload switch
        {
            ChatJobPayload { TaskProfile: not null } =>
                ExecuteRoutedChatAsync(request, cancellationToken),
            ModelMaintenanceJobPayload maintenance =>
                ExecuteMaintenanceAsync(maintenance, cancellationToken),
            ModelControlJobPayload control =>
                ExecuteControlAsync(control, cancellationToken),
            _ => ExecuteDirectAsync(request, cancellationToken)
        };
    }

    private async Task<BrokerExecutionResult> ExecuteDirectAsync(
        LocalJobRequest request,
        CancellationToken cancellationToken)
    {
        TrackResidentModel(ModelFromDirectPayload(request.Payload));
        return await _executeDirect(request, cancellationToken);
    }

    private void TrackResidentModel(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model) &&
            _catalog.Models.Any(candidate =>
                string.Equals(candidate.Tag, model, StringComparison.Ordinal)))
        {
            Volatile.Write(ref _residentModel, model);
        }
    }

    private static string? ModelFromDirectPayload(LocalJobPayload payload) =>
        payload switch
        {
            EmbedJobPayload embed => embed.Model,
            ChatJobPayload chat => chat.Model,
            NativeOllamaJobPayload native when native.Operation is
                NativeOllamaOperation.Chat or
                NativeOllamaOperation.Embed or
                NativeOllamaOperation.Generate => NativeModel(native.RequestBody),
            _ => null
        };

    private static string? NativeModel(JsonElement? requestBody)
    {
        if (requestBody is not { ValueKind: JsonValueKind.Object } body ||
            !body.TryGetProperty("model", out var model) ||
            model.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return model.GetString();
    }

    private async Task<BrokerExecutionResult> ExecuteRoutedChatAsync(
        LocalJobRequest request,
        CancellationToken cancellationToken)
    {
        BrokerExecutionResult result;
        if (_prepared.Remove(request.JobId, out var prepared))
        {
            result = await _coordinator.ExecuteAsync(
                request,
                prepared.Availability,
                prepared.Selection,
                cancellationToken);
        }
        else
        {
            var availability = await AvailabilityAsync(cancellationToken);
            result = await _coordinator.ExecuteAsync(
                request,
                availability,
                cancellationToken);
        }

        if (result.Routing is { } routing)
        {
            TrackResidentModel(routing.SelectedModel);
        }

        return result;
    }

    private async Task<BrokerExecutionResult> ExecuteMaintenanceAsync(
        ModelMaintenanceJobPayload payload,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                payload.CatalogVersion,
                _catalog.CatalogVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Maintenance job catalog '{payload.CatalogVersion}' is stale; " +
                $"current catalog is '{_catalog.CatalogVersion}'.");
        }

        switch (payload.Operation)
        {
            case ModelMaintenanceOperation.Pull:
                await _runtime.PullAsync(payload.Model, cancellationToken);
                return Result(new ModelMaintenanceJobOutput("success"));
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(payload),
                    payload.Operation,
                    "Unsupported model maintenance operation.");
        }
    }

    private async Task<BrokerExecutionResult> ExecuteControlAsync(
        ModelControlJobPayload payload,
        CancellationToken cancellationToken)
    {
        return payload.Operation switch
        {
            ModelControlOperation.Status =>
                Result(await _control.StatusAsync(cancellationToken)),
            ModelControlOperation.Preflight =>
                Result(await PreflightAsync(payload, cancellationToken)),
            ModelControlOperation.ExperimentReport =>
                Result(await _control.ReportAsync(
                    payload.Profile!.Value,
                    payload.Model!,
                    cancellationToken)),
            ModelControlOperation.Feedback =>
                Result(await ApplyFeedbackAsync(payload, cancellationToken)),
            ModelControlOperation.CompleteExperiment =>
                Result(await _control.CompleteExperimentAsync(
                    payload.WorkflowId!.Value,
                    payload.Profile!.Value,
                    payload.Model!,
                    payload.Outcome!.Value,
                    payload.TaskMetrics!,
                    cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Operation,
                "Unsupported model control operation.")
        };
    }

    private async Task<LocalModelPreflightOutput> PreflightAsync(
        ModelControlJobPayload payload,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                payload.CatalogVersion,
                _catalog.CatalogVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Preflight catalog '{payload.CatalogVersion}' is stale; " +
                $"current catalog is '{_catalog.CatalogVersion}'.");
        }

        var proof = await _runtime.EnsureReadyAsync(
            payload.Model!,
            payload.ContextTokens!.Value,
            cancellationToken);
        Volatile.Write(ref _residentModel, proof.Model);
        return new LocalModelPreflightOutput(
            proof.Model,
            proof.ContextTokens,
            _catalog.CatalogVersion,
            proof.SizeBytes,
            proof.SizeVramBytes,
            proof.FullyResident,
            proof.VerifiedAtUtc);
    }

    private async Task<LocalModelFeedbackOutput> ApplyFeedbackAsync(
        ModelControlJobPayload payload,
        CancellationToken cancellationToken)
    {
        await _control.ApplyFeedbackAsync(
            payload.Profile!.Value,
            payload.Model!,
            payload.OwnerAction!.Value,
            cancellationToken);
        return new LocalModelFeedbackOutput(
            payload.Profile.Value,
            payload.Model!,
            payload.OwnerAction.Value);
    }

    private async Task<ModelAvailability> AvailabilityAsync(
        CancellationToken cancellationToken)
    {
        var installed = await _transport.ListInstalledAsync(cancellationToken);
        var processes = await _transport.ListProcessesAsync(cancellationToken);
        var resident = processes
            .Select(process => process.Model)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Volatile.Write(ref _residentModel, resident.FirstOrDefault());
        var disabled = _catalog.Models
            .SelectMany(model => model.ContextTokens.Select(
                context => new ModelContextRef(model.Tag, context)))
            .Where(context => _runtime.IsDisabled(
                context.Model,
                context.ContextTokens))
            .ToArray();
        return new ModelAvailability(installed, resident, disabled);
    }

    private static BrokerExecutionResult Result<T>(T value) =>
        new(JsonSerializer.SerializeToElement(value, LocalAiJson.Strict));

    private sealed record PreparedModelExecution(
        ModelSelection Selection,
        ModelAvailability Availability);
}
