using LocalAi.Contracts;

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
    DateTimeOffset VerifiedAtUtc)
{
    /// <summary>
    /// Set when a relaxed policy admitted a load that is not fully resident, and therefore
    /// slower. Read by the preflight output, which returns it as it stands; the report line a
    /// local tool prints takes the fact from Shortfall() instead, because that line is rendered
    /// by the client in its own language.
    ///
    /// For as long as this was the only carrier, it reached nobody: an English sentence had
    /// nowhere to go in a Russian report line, so degraded answers looked healthy (#277).
    /// </summary>
    public string? DegradationWarning { get; init; }

    /// <summary>
    /// The shortfall as a fact rather than as prose, with the share of the model that reached
    /// video memory.
    ///
    /// <see cref="DegradationWarning"/> is an English sentence, and the line that reports a
    /// local call is rendered by the client in its own language — which is why the warning
    /// travelled no further than this record for as long as it existed. This does travel.
    ///
    /// A size the runtime did not report is not a shortfall: marking a healthy answer as
    /// degraded spends the mark's meaning on noise.
    /// </summary>
    public (ResidencyShortfall Shortfall, int? ResidentPercent) Shortfall()
    {
        if (SizeBytes <= 0)
        {
            return (ResidencyShortfall.None, null);
        }

        if (SizeVramBytes >= SizeBytes)
        {
            // Ollama has reported more in video memory than the model's own size. The honest
            // reading of that is that nothing is missing, not that 103% of it arrived.
            return (ResidencyShortfall.None, 100);
        }

        var percent = (int)(SizeVramBytes * 100 / SizeBytes);
        // Under one percent is the processor running the model, whatever the arithmetic rounds
        // to: reported as a partial offload it renders as "0% of the model in video memory —
        // answers are slower", which says "slower" about the case that is slowest.
        return percent <= 0
            ? (ResidencyShortfall.Cpu, 0)
            : (ResidencyShortfall.PartialOffload, percent);
    }
}

/// <summary>
/// One line of the model backend's pull stream, as facts rather than words. The backend
/// counts per layer, so <c>Completed</c> and <c>Total</c> restart as each digest begins;
/// summing them into one figure is the caller's job, not the transport's.
/// </summary>
public sealed record ModelPullProgress(string Status, string? Digest, long Completed, long Total);

public interface IModelRuntimeTransport
{
    Task<IReadOnlyList<string>> ListInstalledAsync(CancellationToken ct);

    Task<IReadOnlyList<OllamaProcessInfo>> ListProcessesAsync(CancellationToken ct);

    Task PullAsync(
        string model,
        Func<ModelPullProgress, CancellationToken, Task>? onProgress,
        CancellationToken ct);

    Task PreflightAsync(string model, int contextTokens, CancellationToken ct);

    Task PreflightEmbeddingAsync(
        string model,
        int contextTokens,
        CancellationToken ct);

    Task UnloadAsync(string model, CancellationToken ct);
}

public interface IModelRuntime
{
    bool IsDisabled(string model, int contextTokens);

    Task PullAsync(
        string model,
        IJobProgress? progress,
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
    private readonly LocalAi.Contracts.ModelResidencyPolicy _residencyPolicy;
    private readonly HashSet<ModelContextKey> _disabled = [];
    private readonly object _sync = new();

    public ModelRuntime(
        IModelRuntimeTransport transport,
        ModelRoutingCatalog catalog,
        TimeProvider? timeProvider = null,
        LocalAi.Contracts.ModelResidencyPolicy residencyPolicy =
            LocalAi.Contracts.ModelResidencyPolicy.RequireFullVram)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _residencyPolicy = residencyPolicy;
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
        IJobProgress? progress,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.IsMaintenanceAllowed(model))
        {
            throw new ArgumentException(
                $"Model '{model}' is not in the maintenance allowlist.",
                nameof(model));
        }

        if (progress is null)
        {
            await _transport.PullAsync(model, onProgress: null, cancellationToken);
            return;
        }

        var tracker = new ModelPullTracker(() => DateTimeOffset.UtcNow);
        await _transport.PullAsync(
            model,
            async (line, token) =>
            {
                if (tracker.Accept(line) is { } position)
                {
                    await progress.ReportAsync(position, token);
                }
            },
            cancellationToken);
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

            if (entry.Capabilities.Contains(LocalAi.Contracts.LocalModelCapability.Embedding) &&
                !entry.Capabilities.Contains(LocalAi.Contracts.LocalModelCapability.Text))
            {
                await _transport.PreflightEmbeddingAsync(
                    model,
                    contextTokens,
                    cancellationToken);
            }
            else
            {
                await _transport.PreflightAsync(
                    model,
                    contextTokens,
                    cancellationToken);
            }
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

            // A model that reports no size at all never really loaded; that is a technical
            // failure under every policy.
            if (process.SizeBytes <= 0)
            {
                throw new ModelPreflightException(
                    model,
                    contextTokens,
                    LocalAi.Contracts.ModelExecutionOutcome.TechnicalFailure,
                    $"Model '{model}' reported no loaded size after preflight.");
            }

            var fullyResident = process.SizeVramBytes == process.SizeBytes;
            var admitted = _residencyPolicy switch
            {
                LocalAi.Contracts.ModelResidencyPolicy.RequireFullVram =>
                    process.SizeVramBytes > 0 && fullyResident,
                // Partial offload still requires the adapter to hold something; a pure CPU
                // load under this policy is a different, explicitly chosen setting.
                LocalAi.Contracts.ModelResidencyPolicy.AllowPartialOffload =>
                    process.SizeVramBytes > 0,
                LocalAi.Contracts.ModelResidencyPolicy.AllowCpu => true,
                _ => false,
            };

            if (!admitted)
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
                fullyResident,
                _timeProvider.GetUtcNow())
            {
                DegradationWarning = _residencyPolicy.DescribeDegradation(
                    process.SizeBytes,
                    process.SizeVramBytes),
            };
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
