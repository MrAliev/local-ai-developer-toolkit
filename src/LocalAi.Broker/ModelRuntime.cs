namespace LocalAi.Broker;

public sealed record OllamaProcessInfo(
    string Model,
    long SizeBytes,
    long SizeVramBytes,
    int ContextTokens,
    DateTimeOffset ExpiresAtUtc);

public sealed record ModelResidencyProof(
    string Model,
    int ContextTokens,
    long SizeBytes,
    long SizeVramBytes,
    bool FullyResident,
    DateTimeOffset VerifiedAtUtc);

public interface IModelRuntimeTransport
{
    Task<IReadOnlyList<string>> ListInstalledAsync(CancellationToken ct);

    Task<IReadOnlyList<OllamaProcessInfo>> ListProcessesAsync(CancellationToken ct);

    Task PullAsync(string model, CancellationToken ct);

    Task PreflightAsync(string model, int contextTokens, CancellationToken ct);

    Task UnloadAsync(string model, CancellationToken ct);
}

public interface IModelRuntime
{
    bool IsDisabled(string model, int contextTokens);

    Task PullAsync(
        string model,
        CancellationToken cancellationToken = default);

    Task<ModelResidencyProof> EnsureReadyAsync(
        string model,
        int contextTokens,
        CancellationToken cancellationToken = default);
}

public sealed class ModelPreflightException(
    string model,
    int contextTokens,
    LocalAi.Contracts.ModelExecutionOutcome outcome,
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string Model { get; } = model;

    public int ContextTokens { get; } = contextTokens;

    public LocalAi.Contracts.ModelExecutionOutcome Outcome { get; } = outcome;
}

public sealed class ModelRuntime : IModelRuntime
{
    private readonly IModelRuntimeTransport _transport;
    private readonly ModelRoutingCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<ModelContextKey> _disabled = [];
    private readonly object _sync = new();

    public ModelRuntime(
        IModelRuntimeTransport transport,
        ModelRoutingCatalog catalog,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsDisabled(string model, int contextTokens)
    {
        lock (_sync)
        {
            return _disabled.Contains(new ModelContextKey(model, contextTokens));
        }
    }

    public async Task PullAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.IsMaintenanceAllowed(model))
        {
            throw new ArgumentException(
                $"Model '{model}' is not in the maintenance allowlist.",
                nameof(model));
        }

        await _transport.PullAsync(model, cancellationToken);
    }

    public async Task<ModelResidencyProof> EnsureReadyAsync(
        string model,
        int contextTokens,
        CancellationToken cancellationToken = default)
    {
        var entry = _catalog.Model(model);
        if (!entry.ContextTokens.Contains(contextTokens))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextTokens),
                $"Context {contextTokens} is not supported by model '{model}'.");
        }

        var key = new ModelContextKey(model, contextTokens);
        if (IsDisabled(model, contextTokens))
        {
            throw new InvalidOperationException(
                $"Model/context '{model}/{contextTokens}' is disabled.");
        }

        try
        {
            var installed = await _transport.ListInstalledAsync(cancellationToken);
            if (!installed.Contains(model, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Model '{model}' is not installed.");
            }

            var beforePreflight = await _transport.ListProcessesAsync(
                cancellationToken);
            var managedModels = _catalog.Models
                .Select(candidate => candidate.Tag)
                .ToHashSet(StringComparer.Ordinal);
            var otherManagedModels = beforePreflight
                .Select(candidate => candidate.Model)
                .Where(candidate =>
                    !string.Equals(candidate, model, StringComparison.Ordinal) &&
                    managedModels.Contains(candidate))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var otherModel in otherManagedModels)
            {
                await _transport.UnloadAsync(otherModel, cancellationToken);
            }

            var existing = beforePreflight.SingleOrDefault(
                candidate => string.Equals(
                    candidate.Model,
                    model,
                    StringComparison.Ordinal));
            if (existing is not null &&
                existing.ContextTokens != contextTokens)
            {
                await _transport.UnloadAsync(model, cancellationToken);
            }

            await _transport.PreflightAsync(model, contextTokens, cancellationToken);
            var processes = await _transport.ListProcessesAsync(cancellationToken);
            var process = processes.SingleOrDefault(
                candidate => string.Equals(
                    candidate.Model,
                    model,
                    StringComparison.Ordinal));
            if (process is null)
            {
                throw new ModelPreflightException(
                    model,
                    contextTokens,
                    LocalAi.Contracts.ModelExecutionOutcome.TechnicalFailure,
                    $"Model '{model}' is absent from the live process list after preflight.");
            }

            if (process.ContextTokens != contextTokens)
            {
                throw new ModelPreflightException(
                    model,
                    contextTokens,
                    LocalAi.Contracts.ModelExecutionOutcome.ContextFailure,
                    $"Model '{model}' loaded context {process.ContextTokens}, " +
                    $"expected {contextTokens}.");
            }

            if (process.SizeBytes <= 0 ||
                process.SizeVramBytes <= 0 ||
                process.SizeVramBytes != process.SizeBytes)
            {
                throw new ModelPreflightException(
                    model,
                    contextTokens,
                    LocalAi.Contracts.ModelExecutionOutcome.CpuOffload,
                    $"Model '{model}' is not fully resident in VRAM.");
            }

            return new ModelResidencyProof(
                model,
                contextTokens,
                process.SizeBytes,
                process.SizeVramBytes,
                FullyResident: true,
                _timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                _disabled.Add(key);
            }

            try
            {
                await _transport.UnloadAsync(model, cancellationToken);
            }
            catch (Exception unloadException) when (
                unloadException is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Model '{model}' failed preflight and could not be unloaded.",
                    new AggregateException(exception, unloadException));
            }

            if (exception is ModelPreflightException preflight)
            {
                throw preflight;
            }

            throw new ModelPreflightException(
                model,
                contextTokens,
                LocalAi.Contracts.ModelExecutionOutcome.TechnicalFailure,
                $"Model '{model}' failed full-VRAM preflight and was unloaded.",
                exception);
        }
    }

    private sealed record ModelContextKey(string Model, int ContextTokens);
}
