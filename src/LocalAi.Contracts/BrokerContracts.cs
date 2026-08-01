using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalJobKind
{
    Embed,
    Chat,
    ListModels,
    ModelMaintenance,
    ModelControl,
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
public sealed record BrokerCompatibility(
    [property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] string BuildCompatibilityId);

public static class BrokerCompatibilityContract
{
    public const int HostStateSchemaVersion = 3;
    public const int ProtocolVersion = 1;
    public const string BuildCompatibilityId = "localai-broker-v1";

    public static BrokerCompatibility Current { get; } =
        new(ProtocolVersion, BuildCompatibilityId);

    public static bool IsCurrent(BrokerCompatibility? value) =>
        value is not null &&
        value.ProtocolVersion == ProtocolVersion &&
        string.Equals(
            value.BuildCompatibilityId,
            BuildCompatibilityId,
            StringComparison.Ordinal);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerProcessState(
    [property: JsonRequired] int ProcessId,
    [property: JsonRequired] DateTimeOffset StartedAtUtc,
    [property: JsonRequired] DateTimeOffset HeartbeatAtUtc,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string BrokerAssemblyPath,
    BrokerCompatibility? Compatibility = null);

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "$type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(EmbedJobPayload), "embed")]
[JsonDerivedType(typeof(ChatJobPayload), "chat")]
[JsonDerivedType(typeof(ListModelsJobPayload), "listModels")]
[JsonDerivedType(typeof(ModelMaintenanceJobPayload), "modelMaintenance")]
[JsonDerivedType(typeof(ModelControlJobPayload), "modelControl")]
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
    public EmbedJobPayload(
        string? model,
        IReadOnlyList<string>? inputs,
        int? requestedContextTokens = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model cannot be blank.", nameof(model));
        }

        if (requestedContextTokens is { } contextTokens &&
            !LocalContextTiers.IsSupported(contextTokens))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedContextTokens),
                "Context must be a supported power-of-two tier from 2K through 256K.");
        }

        Model = model;
        Inputs = Snapshot(inputs, nameof(inputs), requireValues: true);
        RequestedContextTokens = requestedContextTokens;
    }

    [JsonRequired]
    [JsonInclude]
    public string Model { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public IReadOnlyList<string> Inputs { get; private init; }

    /// <summary>
    /// Embedding inputs are sized by the caller's chunk budget, not by a conversation, so the
    /// tier must travel with the request. Left null the transport sends no options at all and
    /// Ollama falls back to its VRAM-derived default, which was 4096 on a 16 GB iGPU - a dense
    /// C# chunk runs about 1.5 characters per token, so a full-size chunk overflowed that
    /// window and the whole indexing run died on one HTTP 400.
    /// </summary>
    [JsonInclude]
    public int? RequestedContextTokens { get; private init; }

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
        IReadOnlyList<string>? imagesBase64,
        LocalTaskProfile? taskProfile = null,
        LocalWorkloadMetadata? workload = null,
        LocalWorkflowHint? workflow = null,
        int? requestedContextTokens = null)
    {
        if (string.IsNullOrWhiteSpace(model) && taskProfile is null)
        {
            throw new ArgumentException(
                "Either a model or task profile is required.",
                nameof(model));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt cannot be blank.", nameof(prompt));
        }

        if (taskProfile is { } profile && !Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(taskProfile));
        }

        if (taskProfile is not null && workload is null)
        {
            throw new ArgumentNullException(
                nameof(workload),
                "Routed chat requires workload metadata.");
        }

        if (requestedContextTokens is { } contextTokens &&
            !LocalContextTiers.IsSupported(contextTokens))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedContextTokens),
                "Context must be a supported power-of-two tier from 2K through 256K.");
        }

        Model = string.IsNullOrWhiteSpace(model) ? null : model;
        Prompt = prompt;
        System = string.IsNullOrWhiteSpace(system) ? null : system;
        ImagesBase64 = Snapshot(imagesBase64, nameof(imagesBase64), requireValues: false);
        TaskProfile = taskProfile;
        Workload = workload;
        Workflow = workflow;
        RequestedContextTokens = requestedContextTokens;
    }

    [JsonInclude]
    public string? Model { get; private init; }

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

    [JsonInclude]
    public LocalTaskProfile? TaskProfile { get; private init; }

    [JsonInclude]
    public LocalWorkloadMetadata? Workload { get; private init; }

    [JsonInclude]
    public LocalWorkflowHint? Workflow { get; private init; }

    [JsonInclude]
    public int? RequestedContextTokens { get; private init; }
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
public sealed record ModelMaintenanceJobPayload : LocalJobPayload
{
    [JsonConstructor]
    public ModelMaintenanceJobPayload(
        ModelMaintenanceOperation operation,
        string? model,
        string? catalogVersion)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model cannot be blank.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(catalogVersion))
        {
            throw new ArgumentException(
                "Catalog version cannot be blank.",
                nameof(catalogVersion));
        }

        Operation = operation;
        Model = model;
        CatalogVersion = catalogVersion;
    }

    [JsonIgnore]
    public override LocalJobKind Kind => LocalJobKind.ModelMaintenance;

    [JsonRequired]
    [JsonInclude]
    public ModelMaintenanceOperation Operation { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public string Model { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public string CatalogVersion { get; private init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelMaintenanceJobOutput(
    [property: JsonRequired] string Status);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelControlJobPayload : LocalJobPayload
{
    [JsonConstructor]
    public ModelControlJobPayload(
        ModelControlOperation operation,
        LocalTaskProfile? profile,
        string? model,
        ExperimentOwnerAction? ownerAction,
        Guid? workflowId = null,
        ModelExecutionOutcome? outcome = null,
        LocalExperimentTaskMetrics? taskMetrics = null,
        int? contextTokens = null,
        string? catalogVersion = null)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (profile is { } taskProfile && !Enum.IsDefined(taskProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        if (ownerAction is { } action && !Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(ownerAction));
        }

        if (outcome is { } taskOutcome && !Enum.IsDefined(taskOutcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (operation == ModelControlOperation.Status)
        {
            if (profile is not null ||
                model is not null ||
                ownerAction is not null ||
                workflowId is not null ||
                outcome is not null ||
                taskMetrics is not null ||
                contextTokens is not null ||
                catalogVersion is not null)
            {
                throw new ArgumentException(
                    "Status does not accept experiment parameters.");
            }
        }
        else if (operation == ModelControlOperation.Preflight)
        {
            if (profile is not null ||
                string.IsNullOrWhiteSpace(model) ||
                ownerAction is not null ||
                workflowId is not null ||
                outcome is not null ||
                taskMetrics is not null ||
                contextTokens is null or <= 0 ||
                !IsSafeCatalogVersion(catalogVersion))
            {
                throw new ArgumentException(
                    "Preflight requires only a model, positive context tokens, and catalog version.");
            }
        }
        else
        {
            if (profile is null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("Model cannot be blank.", nameof(model));
            }

            if (operation == ModelControlOperation.Feedback && ownerAction is null)
            {
                throw new ArgumentNullException(nameof(ownerAction));
            }

            if (operation is
                    ModelControlOperation.ExperimentReport or
                    ModelControlOperation.Feedback &&
                (workflowId is not null ||
                 outcome is not null ||
                 taskMetrics is not null))
            {
                throw new ArgumentException(
                    "Report and feedback do not accept workflow completion data.");
            }

            if (operation == ModelControlOperation.ExperimentReport &&
                ownerAction is not null)
            {
                throw new ArgumentException(
                    "Experiment report does not accept an owner action.",
                    nameof(ownerAction));
            }

            if (operation == ModelControlOperation.CompleteExperiment)
            {
                if (ownerAction is not null)
                {
                    throw new ArgumentException(
                        "Experiment completion does not accept an owner action.",
                        nameof(ownerAction));
                }

                if (workflowId is null || workflowId == Guid.Empty)
                {
                    throw new ArgumentException(
                        "Experiment completion requires a workflow ID.",
                        nameof(workflowId));
                }

                if (outcome is null)
                {
                    throw new ArgumentNullException(nameof(outcome));
                }

                ArgumentNullException.ThrowIfNull(taskMetrics);
            }

            if (contextTokens is not null)
            {
                throw new ArgumentException(
                    "Experiment operations do not accept context tokens.",
                    nameof(contextTokens));
            }

            if (catalogVersion is not null)
            {
                throw new ArgumentException(
                    "Experiment operations do not accept a catalog version.",
                    nameof(catalogVersion));
            }
        }

        Operation = operation;
        Profile = profile;
        Model = string.IsNullOrWhiteSpace(model) ? null : model;
        OwnerAction = ownerAction;
        WorkflowId = workflowId;
        Outcome = outcome;
        TaskMetrics = taskMetrics;
        ContextTokens = contextTokens;
        CatalogVersion = catalogVersion;
    }

    [JsonIgnore]
    public override LocalJobKind Kind => LocalJobKind.ModelControl;

    [JsonRequired]
    [JsonInclude]
    public ModelControlOperation Operation { get; private init; }

    [JsonInclude]
    public LocalTaskProfile? Profile { get; private init; }

    [JsonInclude]
    public string? Model { get; private init; }

    [JsonInclude]
    public ExperimentOwnerAction? OwnerAction { get; private init; }

    [JsonInclude]
    public Guid? WorkflowId { get; private init; }

    [JsonInclude]
    public ModelExecutionOutcome? Outcome { get; private init; }

    [JsonInclude]
    public LocalExperimentTaskMetrics? TaskMetrics { get; private init; }

    [JsonInclude]
    public int? ContextTokens { get; private init; }

    [JsonInclude]
    public string? CatalogVersion { get; private init; }

    private static bool IsSafeCatalogVersion(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= 128 &&
        IsAsciiAlphaNumeric(value[0]) &&
        IsAsciiAlphaNumeric(value[^1]) &&
        value.All(character =>
            IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
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
    string? GitTree,
    LocalRoutingReceipt? Routing = null);

public sealed record LocalRoutingReceipt(
    LocalTaskProfile? TaskProfile,
    string SelectedModel,
    int? ContextTokens,
    bool WasCold,
    bool UsedFallback,
    string? ValidatorResult,
    long EstimatedGrossCloudTokensSaved,
    long EstimatedVerificationTokens,
    long EstimatedNetCloudTokensSaved,
    bool IsExperimentalAttempt = false,
    string? ExperimentalModel = null,
    ModelExecutionOutcome? ExperimentalOutcome = null);

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
        DateTimeOffset? createdAtUtc = null,
        int? requestedContextTokens = null) =>
        Create(
            deduplicationKey,
            priority,
            new EmbedJobPayload(model, inputs, requestedContextTokens),
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

    public static LocalJobRequest CreateRoutedChat(
        string? deduplicationKey,
        LocalJobPriority priority,
        LocalTaskProfile taskProfile,
        string? prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64,
        LocalWorkloadMetadata workload,
        LocalWorkflowHint? workflow = null,
        int? requestedContextTokens = null,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new ChatJobPayload(
                null,
                prompt,
                system,
                imagesBase64,
                taskProfile,
                workload,
                workflow,
                requestedContextTokens),
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

    public static LocalJobRequest CreateModelMaintenance(
        string? deduplicationKey,
        LocalJobPriority priority,
        ModelMaintenanceOperation operation,
        string? model,
        string? catalogVersion,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new ModelMaintenanceJobPayload(operation, model, catalogVersion),
            jobId,
            createdAtUtc);

    public static LocalJobRequest CreateModelControl(
        string? deduplicationKey,
        LocalJobPriority priority,
        ModelControlOperation operation,
        LocalTaskProfile? profile = null,
        string? model = null,
        ExperimentOwnerAction? ownerAction = null,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new ModelControlJobPayload(operation, profile, model, ownerAction),
            jobId,
            createdAtUtc);

    public static LocalJobRequest CreateModelPreflight(
        string? deduplicationKey,
        LocalJobPriority priority,
        string model,
        int contextTokens,
        string catalogVersion,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new ModelControlJobPayload(
                ModelControlOperation.Preflight,
                profile: null,
                model,
                ownerAction: null,
                contextTokens: contextTokens,
                catalogVersion: catalogVersion),
            jobId,
            createdAtUtc);

    public static LocalJobRequest CreateExperimentCompletion(
        string? deduplicationKey,
        LocalJobPriority priority,
        Guid workflowId,
        LocalTaskProfile profile,
        string model,
        ModelExecutionOutcome outcome,
        LocalExperimentTaskMetrics taskMetrics,
        Guid? jobId = null,
        DateTimeOffset? createdAtUtc = null) =>
        Create(
            deduplicationKey,
            priority,
            new ModelControlJobPayload(
                ModelControlOperation.CompleteExperiment,
                profile,
                model,
                ownerAction: null,
                workflowId,
                outcome,
                taskMetrics),
            jobId,
            createdAtUtc);

    public static LocalJobRequest ResolveRoutedChat(
        LocalJobRequest request,
        string model)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var chat = request.Payload as ChatJobPayload
            ?? throw new ArgumentException(
                "Only chat requests can be model-resolved.",
                nameof(request));
        if (chat.TaskProfile is null)
        {
            throw new ArgumentException(
                "Chat request is not routed.",
                nameof(request));
        }

        return new LocalJobRequest(
            request.JobId,
            request.DeduplicationKey,
            request.Priority,
            new ChatJobPayload(
                model,
                chat.Prompt,
                chat.System,
                chat.ImagesBase64,
                chat.TaskProfile,
                chat.Workload,
                chat.Workflow,
                chat.RequestedContextTokens),
            request.CreatedAtUtc);
    }

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
