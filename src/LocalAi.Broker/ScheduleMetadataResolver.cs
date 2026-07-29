using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class ScheduleMetadataResolver(
    ModelRoutingCatalog catalog,
    DurationEstimator estimator)
{
    private readonly ModelRoutingCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly DurationEstimator _estimator =
        estimator ?? throw new ArgumentNullException(nameof(estimator));

    public ScheduledJobCandidate Resolve(
        QueuedJobCandidate candidate,
        string? selectedModel = null,
        string? residentModel = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var (profile, model, workload, durationClass, isMaintenance) =
            Describe(candidate.Request.Payload);
        var key = CreateKey(
            profile,
            selectedModel ?? model,
            isCold: !string.Equals(
                selectedModel ?? model,
                residentModel,
                StringComparison.Ordinal),
            workload,
            durationClass);
        var prediction = _estimator.Predict(key);
        var dependencyReady =
            (candidate.Request.Payload as ChatJobPayload)?
                .Workflow?.IsDependencyReady != false;
        return new ScheduledJobCandidate(
            candidate.Request.JobId,
            candidate.Sequence,
            candidate.Request.Priority,
            candidate.CreatedAtUtc,
            selectedModel ?? model,
            prediction.Median,
            durationClass,
            dependencyReady,
            isMaintenance);
    }

    public void Observe(
        LocalJobRequest request,
        LocalRoutingReceipt? routing,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (profile, describedModel, workload, durationClass, _) =
            Describe(request.Payload);
        var key = CreateKey(
            profile,
            routing?.SelectedModel ?? describedModel,
            routing?.WasCold ?? false,
            workload,
            durationClass);
        _estimator.Observe(key, duration);
    }

    private static DurationObservationKey CreateKey(
        LocalTaskProfile profile,
        string model,
        bool isCold,
        LocalWorkloadMetadata? workload,
        LocalDurationClass durationClass) =>
        new(
            profile,
            model,
            Size(workload?.InputCharacters ?? 0),
            Size(workload?.ExpectedOutputCharacters ?? 0),
            Count(workload?.FileCount ?? 0),
            Count(workload?.ImageCount ?? 0),
            Images(workload?.TotalImagePixels ?? 0),
            isCold,
            durationClass);

    private (
        LocalTaskProfile Profile,
        string Model,
        LocalWorkloadMetadata? Workload,
        LocalDurationClass DurationClass,
        bool IsMaintenance) Describe(LocalJobPayload payload) =>
        payload switch
        {
            ChatJobPayload chat => DescribeChat(chat),
            EmbedJobPayload embed => (
                LocalTaskProfile.VectorEmbedding,
                embed.Model,
                null,
                LocalDurationClass.Short,
                false),
            ModelMaintenanceJobPayload maintenance => (
                LocalTaskProfile.Planning,
                maintenance.Model,
                null,
                LocalDurationClass.Long,
                true),
            ModelControlJobPayload control => (
                control.Profile ?? LocalTaskProfile.Classification,
                control.Model ?? "model-control",
                null,
                LocalDurationClass.Short,
                false),
            ListModelsJobPayload => (
                LocalTaskProfile.Classification,
                "model-control",
                null,
                LocalDurationClass.Short,
                false),
            NativeOllamaJobPayload native => (
                LocalTaskProfile.CodeAnalysis,
                NativeModel(native.RequestBody),
                null,
                LocalDurationClass.Medium,
                false),
            _ => throw new ArgumentOutOfRangeException(nameof(payload))
        };

    private (
        LocalTaskProfile Profile,
        string Model,
        LocalWorkloadMetadata? Workload,
        LocalDurationClass DurationClass,
        bool IsMaintenance) DescribeChat(ChatJobPayload chat)
    {
        if (chat.TaskProfile is not { } profile)
        {
            return (
                LocalTaskProfile.CodeAnalysis,
                chat.Model!,
                chat.Workload,
                chat.Workload?.DurationClass ?? LocalDurationClass.Medium,
                false);
        }

        var route = _catalog.Route(profile);
        var model = chat.Model ??
                    route.Candidates.FirstOrDefault() ??
                    route.Fallbacks.FirstOrDefault() ??
                    "deterministic";
        return (
            profile,
            model,
            chat.Workload,
            chat.Workload?.DurationClass ?? route.DefaultDuration,
            false);
    }

    private static LocalSizeBucket Size(int characters) =>
        characters switch
        {
            0 => LocalSizeBucket.Empty,
            <= 4_000 => LocalSizeBucket.Small,
            <= 16_000 => LocalSizeBucket.Medium,
            _ => LocalSizeBucket.Large
        };

    private static LocalCountBucket Count(int count) =>
        count switch
        {
            0 => LocalCountBucket.None,
            1 => LocalCountBucket.One,
            <= 4 => LocalCountBucket.Few,
            _ => LocalCountBucket.Many
        };

    private static LocalImageBucket Images(long pixels) =>
        pixels switch
        {
            0 => LocalImageBucket.None,
            <= 1_048_576 => LocalImageBucket.Small,
            <= 4_194_304 => LocalImageBucket.Medium,
            _ => LocalImageBucket.Large
        };

    private static string NativeModel(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("model", out var model) &&
        model.ValueKind == JsonValueKind.String
            ? model.GetString() ?? "native-ollama"
            : "native-ollama";
}
