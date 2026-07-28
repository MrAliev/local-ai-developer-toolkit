using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalJobKind
{
    Embed,
    Chat,
    ListModels,
    NativeOllama
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalJobPriority
{
    Interactive = 0,
    Foreground = 10,
    Background = 20
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerProcessState(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    int SchemaVersion);

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "$type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(EmbedJobPayload), "embed")]
[JsonDerivedType(typeof(ChatJobPayload), "chat")]
[JsonDerivedType(typeof(ListModelsJobPayload), "listModels")]
[JsonDerivedType(typeof(NativeOllamaJobPayload), "nativeOllama")]
public abstract record LocalJobPayload
{
    private protected LocalJobPayload()
    {
    }

    [JsonIgnore]
    public abstract LocalJobKind Kind { get; }

    protected static IReadOnlyList<string> Snapshot(
        IReadOnlyList<string>? values,
        string parameterName,
        bool requireValues)
    {
        var snapshot = Array.AsReadOnly(values?.ToArray() ?? []);
        if (requireValues && snapshot.Count == 0)
        {
            throw new ArgumentException(
                "At least one value is required.",
                parameterName);
        }

        if (snapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Values cannot contain blank entries.",
                parameterName);
        }

        return snapshot;
    }
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum NativeOllamaOperation
{
    Chat,
    Embed,
    Tags,
    Show,
    Processes,
    Generate
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeOllamaJobPayload : LocalJobPayload
{
    [JsonConstructor]
    public NativeOllamaJobPayload(
        NativeOllamaOperation operation,
        JsonElement? requestBody)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Operation = operation;
        RequestBody = requestBody?.Clone();
    }

    [JsonIgnore]
    public override LocalJobKind Kind => LocalJobKind.NativeOllama;

    [JsonRequired]
    [JsonInclude]
    public NativeOllamaOperation Operation { get; private init; }

    [JsonInclude]
    public JsonElement? RequestBody { get; private init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeOllamaJobOutput(
    [property: JsonRequired] JsonElement Response);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EmbedJobPayload : LocalJobPayload
{
    [JsonConstructor]
    public EmbedJobPayload(string? model, IReadOnlyList<string>? inputs)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model cannot be blank.", nameof(model));
        }

        Model = model;
        Inputs = Snapshot(inputs, nameof(inputs), requireValues: true);
    }

    [JsonRequired]
    [JsonInclude]
    public string Model { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public IReadOnlyList<string> Inputs { get; private init; }

    [JsonIgnore]
    public override LocalJobKind Kind => LocalJobKind.Embed;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatJobPayload : LocalJobPayload
{
    [JsonConstructor]
    public ChatJobPayload(
        string? model,
        string? prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model cannot be blank.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt cannot be blank.", nameof(prompt));
        }

        Model = model;
        Prompt = prompt;
        System = string.IsNullOrWhiteSpace(system) ? null : system;
        ImagesBase64 = Snapshot(imagesBase64, nameof(imagesBase64), requireValues: false);
    }

    [JsonRequired]
    [JsonInclude]
    public string Model { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public string Prompt { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public string? System { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public IReadOnlyList<string> ImagesBase64 { get; private init; }

    [JsonIgnore]
    public override LocalJobKind Kind => LocalJobKind.Chat;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListModelsJobPayload : LocalJobPayload
{
    [JsonConstructor]
    public ListModelsJobPayload()
    {
    }

    [JsonIgnore]
    public override LocalJobKind Kind => LocalJobKind.ListModels;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalJobRequest
{
    [JsonConstructor]
    public LocalJobRequest(
        Guid jobId,
        string? deduplicationKey,
        LocalJobPriority priority,
        LocalJobPayload? payload,
        DateTimeOffset createdAtUtc)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException(
                "Job ID cannot be empty.",
                nameof(jobId));
        }

        if (createdAtUtc == default)
        {
            throw new ArgumentException(
                "Created timestamp cannot be the default value.",
                nameof(createdAtUtc));
        }

        if (string.IsNullOrWhiteSpace(deduplicationKey))
        {
            throw new ArgumentException(
                "Deduplication key cannot be blank.",
                nameof(deduplicationKey));
        }

        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(
                nameof(priority),
                priority,
                "Priority must be a defined value.");
        }

        JobId = jobId;
        DeduplicationKey = deduplicationKey;
        Priority = priority;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    [JsonRequired]
    [JsonInclude]
    public Guid JobId { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public string DeduplicationKey { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public LocalJobPriority Priority { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public LocalJobPayload Payload { get; private init; }

    [JsonIgnore]
    public LocalJobKind Kind => Payload.Kind;

    [JsonRequired]
    [JsonInclude]
    public DateTimeOffset CreatedAtUtc { get; private init; }
}

public sealed record LocalUsageReceipt(
    Guid JobId,
    string Tool,
    string Operation,
    string Model,
    TimeSpan QueueDuration,
    TimeSpan ExecutionDuration,
    long InputCharacters,
    long EstimatedCloudTokensSaved,
    string? RepositoryId,
    string? GenerationId,
    string? GitTree);

public sealed record LocalJobResult<T>(T Value, LocalUsageReceipt Receipt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerResponseEnvelope(
    [property: JsonRequired] JsonElement Value,
    [property: JsonRequired] LocalUsageReceipt Receipt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EmbedJobOutput
{
    [JsonConstructor]
    public EmbedJobOutput(IReadOnlyList<IReadOnlyList<double>>? embeddings)
    {
        var vectors = embeddings?.Select(
            vector => (IReadOnlyList<double>)Array.AsReadOnly(
                vector?.ToArray() ?? throw new ArgumentException(
                    "Embedding vectors cannot be null.",
                    nameof(embeddings))))
            .ToArray() ?? throw new ArgumentNullException(nameof(embeddings));
        Embeddings = Array.AsReadOnly(vectors);
    }

    [JsonRequired]
    [JsonInclude]
    public IReadOnlyList<IReadOnlyList<double>> Embeddings { get; private init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatJobOutput
{
    [JsonConstructor]
    public ChatJobOutput(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Content cannot be blank.", nameof(content));
        }

        Content = content;
    }

    [JsonRequired]
    [JsonInclude]
    public string Content { get; private init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListModelsJobOutput
{
    [JsonConstructor]
    public ListModelsJobOutput(IReadOnlyList<string>? models)
    {
        var snapshot = Array.AsReadOnly(
            models?.ToArray() ?? throw new ArgumentNullException(nameof(models)));
        if (snapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Models cannot contain blank entries.",
                nameof(models));
        }

        Models = snapshot;
    }

    [JsonRequired]
    [JsonInclude]
    public IReadOnlyList<string> Models { get; private init; }
}

public static class LocalJobRequestFactory
{
    public static LocalJobRequest Create(
        string? deduplicationKey,
        LocalJobPriority priority,
        LocalJobPayload payload,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null)
    {
        return new LocalJobRequest(
            jobId ?? Guid.NewGuid(),
            deduplicationKey,
            priority,
            payload,
            createdAtUtc ?? DateTimeOffset.UtcNow);
    }

    public static LocalJobRequest CreateEmbed(
        string? deduplicationKey,
        LocalJobPriority priority,
        string? model,
        IReadOnlyList<string>? inputs,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new EmbedJobPayload(model, inputs),
            jobId,
            createdAtUtc);

    public static LocalJobRequest CreateChat(
        string? deduplicationKey,
        LocalJobPriority priority,
        string? model,
        string? prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new ChatJobPayload(model, prompt, system, imagesBase64),
            jobId,
            createdAtUtc);

    public static LocalJobRequest CreateListModels(
        string? deduplicationKey,
        LocalJobPriority priority,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new ListModelsJobPayload(),
            jobId,
            createdAtUtc);

    public static LocalJobRequest CreateNativeOllama(
        string? deduplicationKey,
        LocalJobPriority priority,
        NativeOllamaOperation operation,
        JsonElement? requestBody,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new NativeOllamaJobPayload(operation, requestBody),
            jobId,
            createdAtUtc);
}

internal sealed class StrictJsonStringEnumConverter : JsonStringEnumConverter
{
    public StrictJsonStringEnumConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
