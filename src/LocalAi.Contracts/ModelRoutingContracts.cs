using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

public static class LocalContextTiers
{
    public static bool IsSupported(int tokens) =>
        tokens is
            2048 or
            4096 or
            8192 or
            16384 or
            32768 or
            65536 or
            131072 or
            262144;
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalTaskProfile
{
    PlainTranslation,
    TechnicalTranslation,
    ImageTranslation,
    Ocr,
    VisualAnalysis,
    VectorEmbedding,
    ExactSearch,
    CodeRerank,
    CodeAnalysis,
    CodeEditing,
    CodeReview,
    LogTriage,
    Extraction,
    Classification,
    ShortSummary,
    MultiFileSynthesis,
    Planning
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalModelLifecycle
{
    Established,
    Experimental,
    Recommended,
    Disabled
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalModelInstallPolicy
{
    Existing,
    Recommended,
    Manual,
    Never
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalModelCapability
{
    Text,
    Translation,
    Vision,
    Ocr,
    Embedding,
    Code,
    Reasoning
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalRouteMode
{
    Model,
    Deterministic
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalDurationClass
{
    Short,
    Medium,
    Long
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum LocalOutputValidator
{
    None,
    TranslationStructure,
    OcrCompleteness,
    CodeReferences,
    StructuredOutput
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum ModelMaintenanceOperation
{
    Pull
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum ModelControlOperation
{
    Status,
    Preflight,
    ExperimentReport,
    Feedback,
    CompleteExperiment
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum ModelExecutionOutcome
{
    Success,
    TechnicalFailure,
    StructuralFailure,
    ContextFailure,
    CpuOffload
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum ExperimentOwnerAction
{
    Promote,
    ContinueExperiment,
    FallbackOnly,
    Disable
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalWorkloadMetadata
{
    [JsonConstructor]
    public LocalWorkloadMetadata(
        int inputCharacters,
        int expectedOutputCharacters,
        int fileCount,
        int imageCount,
        long totalImagePixels,
        LocalDurationClass durationClass)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedOutputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(fileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(imageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(totalImagePixels);
        if (!Enum.IsDefined(durationClass))
        {
            throw new ArgumentOutOfRangeException(nameof(durationClass));
        }

        InputCharacters = inputCharacters;
        ExpectedOutputCharacters = expectedOutputCharacters;
        FileCount = fileCount;
        ImageCount = imageCount;
        TotalImagePixels = totalImagePixels;
        DurationClass = durationClass;
    }

    [JsonRequired]
    [JsonInclude]
    public int InputCharacters { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public int ExpectedOutputCharacters { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public int FileCount { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public int ImageCount { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public long TotalImagePixels { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public LocalDurationClass DurationClass { get; private init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalWorkflowHint
{
    [JsonConstructor]
    public LocalWorkflowHint(
        Guid workflowId,
        int stepIndex,
        int expectedStepCount,
        IReadOnlyList<LocalTaskProfile>? expectedProfiles,
        bool isDependencyReady)
    {
        if (workflowId == Guid.Empty)
        {
            throw new ArgumentException("Workflow ID cannot be empty.", nameof(workflowId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(stepIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedStepCount, 1);
        if (stepIndex >= expectedStepCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stepIndex),
                "Step index must be smaller than the expected step count.");
        }

        var profiles = Array.AsReadOnly(
            expectedProfiles?.ToArray()
            ?? throw new ArgumentNullException(nameof(expectedProfiles)));
        if (profiles.Count != expectedStepCount ||
            profiles.Any(profile => !Enum.IsDefined(profile)))
        {
            throw new ArgumentException(
                "Expected profiles must define every workflow step.",
                nameof(expectedProfiles));
        }

        WorkflowId = workflowId;
        StepIndex = stepIndex;
        ExpectedStepCount = expectedStepCount;
        ExpectedProfiles = profiles;
        IsDependencyReady = isDependencyReady;
    }

    [JsonRequired]
    [JsonInclude]
    public Guid WorkflowId { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public int StepIndex { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public int ExpectedStepCount { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public IReadOnlyList<LocalTaskProfile> ExpectedProfiles { get; private init; }

    [JsonRequired]
    [JsonInclude]
    public bool IsDependencyReady { get; private init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelCatalogEntry(
    [property: JsonRequired] string Tag,
    [property: JsonRequired] string Source,
    [property: JsonRequired] LocalModelLifecycle Lifecycle,
    [property: JsonRequired] LocalModelInstallPolicy InstallPolicy,
    [property: JsonRequired] IReadOnlyList<LocalModelCapability> Capabilities,
    [property: JsonRequired] IReadOnlyList<int> ContextTokens,
    [property: JsonRequired] bool SupportsImages,
    long? MaxImagePixels);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskRouteEntry(
    [property: JsonRequired] LocalTaskProfile Profile,
    [property: JsonRequired] LocalRouteMode Mode,
    [property: JsonRequired] IReadOnlyList<string> Candidates,
    [property: JsonRequired] IReadOnlyList<string> Fallbacks,
    [property: JsonRequired] LocalOutputValidator Validator,
    [property: JsonRequired] LocalDurationClass DefaultDuration);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelRoutingCatalogDocument(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string CatalogVersion,
    [property: JsonRequired] IReadOnlyList<ModelCatalogEntry> Models,
    [property: JsonRequired] IReadOnlyList<TaskRouteEntry> Routes,
    [property: JsonRequired] IReadOnlyList<string> MaintenanceAllowlist);

public sealed record LocalExperimentPairStatus(
    LocalTaskProfile Profile,
    string Model,
    int CompletedAttempts,
    bool IsPaused,
    bool IsCircuitOpen,
    bool IsPromoted,
    ExperimentOwnerAction? OwnerAction);

public sealed record ModelContextRef(string Model, int ContextTokens);

public sealed record LocalModelsStatusOutput(
    IReadOnlyList<string> InstalledModels,
    IReadOnlyList<string> ResidentModels,
    IReadOnlyList<string> RecommendedMissingModels,
    IReadOnlyList<LocalExperimentPairStatus> Experiments,
    string CatalogVersion,
    IReadOnlyList<LocalModelResidencyStatus>? Residency = null,
    IReadOnlyList<ModelContextRef>? DisabledContexts = null,
    IReadOnlyList<string>? PendingPullModels = null);

public sealed record LocalModelResidencyStatus(
    string Model,
    int ContextTokens,
    long SizeBytes,
    long SizeVramBytes,
    bool FullyResident,
    DateTimeOffset ExpiresAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalModelPreflightOutput(
    [property: JsonRequired] string Model,
    [property: JsonRequired] int ContextTokens,
    [property: JsonRequired] string CatalogVersion,
    [property: JsonRequired] long SizeBytes,
    [property: JsonRequired] long SizeVramBytes,
    [property: JsonRequired] bool FullyResident,
    [property: JsonRequired] DateTimeOffset VerifiedAtUtc);

public sealed record LocalExperimentReportOutput(
    LocalTaskProfile Profile,
    string Model,
    int Attempts,
    int Successes,
    int Errors,
    int Fallbacks,
    TimeSpan MeanTotalDuration,
    TimeSpan MedianTotalDuration,
    TimeSpan P90TotalDuration,
    int ColdExecutions,
    int WarmExecutions,
    long EstimatedNetCloudTokensSaved,
    long LocalTokensProcessed = 0,
    long EstimatedCloudGenerationTokensSaved = 0,
    long EstimatedNetCloudContextTokensSaved = 0,
    // How many of Attempts still have a telemetry record behind them. Attempts, Successes and
    // Errors come from the experiment state, which is what the ten-attempt pause rule counts and
    // is authoritative; the durations and token figures can only be measured over the records
    // that exist. Reporting one number for both is what made a six-attempt pair answer "1".
    int ObservedTasks = 0);

public sealed record LocalExperimentTaskMetrics(
    int InputTokens,
    int OutputTokens,
    int LocalTokensProcessed,
    int EstimatedCloudGenerationTokensSaved,
    int EstimatedNetCloudContextTokensSaved,
    TimeSpan TotalDuration,
    int ColdExecutions,
    int WarmExecutions,
    bool UsedFallback);

public sealed record LocalExperimentCompletionOutput(
    Guid WorkflowId,
    LocalTaskProfile Profile,
    string Model,
    ModelExecutionOutcome Outcome);

public sealed record LocalModelFeedbackOutput(
    LocalTaskProfile Profile,
    string Model,
    ExperimentOwnerAction Action);
