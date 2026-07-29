using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerContractTests
{
    public static TheoryData<LocalJobKind> JobKinds =>
        new(Enum.GetValues<LocalJobKind>());

    public static TheoryData<LocalJobPriority> JobPriorities =>
        new(Enum.GetValues<LocalJobPriority>());

    public static TheoryData<LocalJobState> JobStates =>
        new(Enum.GetValues<LocalJobState>());

    public static TheoryData<LocalTaskProfile> TaskProfiles =>
        new(Enum.GetValues<LocalTaskProfile>());

    public static TheoryData<LocalModelLifecycle> ModelLifecycles =>
        new(Enum.GetValues<LocalModelLifecycle>());

    [Theory]
    [MemberData(nameof(JobKinds))]
    public void JobKind_serializes_as_a_string_and_round_trips(LocalJobKind value) =>
        AssertJsonStringRoundTrip(value);

    [Theory]
    [MemberData(nameof(JobPriorities))]
    public void JobPriority_serializes_as_a_string_and_round_trips(LocalJobPriority value) =>
        AssertJsonStringRoundTrip(value);

    [Theory]
    [MemberData(nameof(JobStates))]
    public void JobState_serializes_as_a_string_and_round_trips(LocalJobState value) =>
        AssertJsonStringRoundTrip(value);

    [Theory]
    [MemberData(nameof(TaskProfiles))]
    public void TaskProfile_serializes_as_a_string_and_round_trips(LocalTaskProfile value) =>
        AssertJsonStringRoundTrip(value);

    [Theory]
    [MemberData(nameof(ModelLifecycles))]
    public void ModelLifecycle_serializes_as_a_string_and_round_trips(
        LocalModelLifecycle value) =>
        AssertJsonStringRoundTrip(value);

    [Fact]
    public void Routed_chat_carries_workload_and_workflow_without_a_concrete_model()
    {
        var workflowId = Guid.NewGuid();

        var request = LocalJobRequestFactory.CreateRoutedChat(
            "routed-chat",
            LocalJobPriority.Foreground,
            LocalTaskProfile.TechnicalTranslation,
            "translate",
            "preserve structure",
            [],
            new LocalWorkloadMetadata(
                1200,
                1400,
                1,
                0,
                0,
                LocalDurationClass.Short),
            new LocalWorkflowHint(
                workflowId,
                0,
                2,
                [
                    LocalTaskProfile.TechnicalTranslation,
                    LocalTaskProfile.TechnicalTranslation
                ],
                isDependencyReady: true),
            requestedContextTokens: 2048);

        var payload = Assert.IsType<ChatJobPayload>(request.Payload);
        Assert.Null(payload.Model);
        Assert.Equal(LocalTaskProfile.TechnicalTranslation, payload.TaskProfile);
        Assert.Equal(1200, payload.Workload!.InputCharacters);
        Assert.Equal(workflowId, payload.Workflow!.WorkflowId);
        Assert.Equal(2048, payload.RequestedContextTokens);
    }

    [Fact]
    public void Legacy_chat_remains_an_explicit_model_override()
    {
        var request = LocalJobRequestFactory.CreateChat(
            "legacy-chat",
            LocalJobPriority.Foreground,
            "qwen3.5:9b",
            "prompt",
            null,
            []);

        var roundTrip = JsonSerializer.Deserialize<LocalJobRequest>(
            JsonSerializer.Serialize(request),
            LocalAiJson.Strict)!;
        var payload = Assert.IsType<ChatJobPayload>(roundTrip.Payload);

        Assert.Equal("qwen3.5:9b", payload.Model);
        Assert.Null(payload.TaskProfile);
        Assert.Null(payload.Workload);
        Assert.Null(payload.Workflow);
    }

    [Fact]
    public void Create_uses_supplied_identity_timestamp_and_payload()
    {
        var jobId = Guid.NewGuid();
        var createdAtUtc = new DateTimeOffset(2026, 7, 28, 9, 30, 0, TimeSpan.Zero);
        var payload = new EmbedJobPayload("embedding-model", ["first", "second"]);

        var request = LocalJobRequestFactory.Create(
            "dedupe-1",
            LocalJobPriority.Foreground,
            payload,
            jobId,
            createdAtUtc);

        Assert.Equal(jobId, request.JobId);
        Assert.Equal(createdAtUtc, request.CreatedAtUtc);
        Assert.Equal("dedupe-1", request.DeduplicationKey);
        Assert.Equal(LocalJobKind.Embed, request.Kind);
        Assert.Equal(LocalJobPriority.Foreground, request.Priority);
        Assert.Same(payload, request.Payload);
    }

    [Fact]
    public void Create_generates_identity_and_defaults_timestamp_to_utc()
    {
        var before = DateTimeOffset.UtcNow;

        var request = LocalJobRequestFactory.CreateListModels(
            "dedupe-1",
            LocalJobPriority.Interactive);

        var after = DateTimeOffset.UtcNow;
        Assert.NotEqual(Guid.Empty, request.JobId);
        Assert.InRange(request.CreatedAtUtc, before, after);
        Assert.Equal(TimeSpan.Zero, request.CreatedAtUtc.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_rejects_blank_deduplication_key(string? deduplicationKey)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            LocalJobRequestFactory.CreateListModels(
                deduplicationKey,
                LocalJobPriority.Interactive));

        Assert.Equal("deduplicationKey", exception.ParamName);
        Assert.Contains("cannot be blank", exception.Message);
    }

    [Fact]
    public void Direct_construction_rejects_empty_job_identity()
    {
        var exception = Assert.Throws<ArgumentException>(() => new LocalJobRequest(
            Guid.Empty,
            "dedupe-1",
            LocalJobPriority.Interactive,
            new ListModelsJobPayload(),
            DateTimeOffset.UtcNow));

        Assert.Equal("jobId", exception.ParamName);
    }

    [Fact]
    public void Direct_construction_rejects_default_created_timestamp()
    {
        var exception = Assert.Throws<ArgumentException>(() => new LocalJobRequest(
            Guid.NewGuid(),
            "dedupe-1",
            LocalJobPriority.Interactive,
            new ListModelsJobPayload(),
            default));

        Assert.Equal("createdAtUtc", exception.ParamName);
    }

    [Fact]
    public void Direct_construction_rejects_undefined_priority()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new LocalJobRequest(
            Guid.NewGuid(),
            "dedupe-1",
            (LocalJobPriority)99,
            new ListModelsJobPayload(),
            DateTimeOffset.UtcNow));

        Assert.Equal("priority", exception.ParamName);
    }

    [Fact]
    public void Json_deserialization_rejects_undefined_priority()
    {
        const string json =
            """
            {
              "JobId": "9cf83dbc-fdd1-4ea9-be4f-bd18c191e63f",
              "DeduplicationKey": "dedupe-1",
              "Priority": "Undefined",
              "Payload": { "$type": "listModels" },
              "CreatedAtUtc": "2026-07-28T09:30:00+00:00"
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LocalJobRequest>(json));
    }

    [Fact]
    public void Json_deserialization_rejects_unknown_request_member()
    {
        var request = LocalJobRequestFactory.CreateListModels(
            "dedupe-1",
            LocalJobPriority.Interactive);
        var json = JsonSerializer.Serialize(request);
        json = json.Insert(json.LastIndexOf('}'), ",\"Unknown\":\"value\"");

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LocalJobRequest>(json));
    }

    [Theory]
    [InlineData("JobId")]
    [InlineData("DeduplicationKey")]
    [InlineData("Priority")]
    [InlineData("Payload")]
    [InlineData("CreatedAtUtc")]
    public void Json_deserialization_rejects_each_missing_required_request_member(string member)
    {
        var values = new Dictionary<string, object?>
        {
            ["JobId"] = Guid.NewGuid(),
            ["DeduplicationKey"] = "dedupe-1",
            ["Priority"] = LocalJobPriority.Interactive,
            ["Payload"] = new ListModelsJobPayload(),
            ["CreatedAtUtc"] = DateTimeOffset.UtcNow
        };
        values.Remove(member);
        var json = JsonSerializer.Serialize(values);

        var exception = Record.Exception(
            () => JsonSerializer.Deserialize<LocalJobRequest>(json));

        Assert.True(
            exception is JsonException or ArgumentException,
            $"Expected required-member rejection for '{member}', but got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Fact]
    public void Valid_request_json_round_trips_with_concrete_payload()
    {
        var request = LocalJobRequestFactory.CreateChat(
            "dedupe-1",
            LocalJobPriority.Background,
            "chat-model",
            "prompt",
            "system",
            []);

        var roundTrip = JsonSerializer.Deserialize<LocalJobRequest>(
            JsonSerializer.Serialize(request));

        Assert.NotNull(roundTrip);
        var payload = Assert.IsType<ChatJobPayload>(roundTrip.Payload);
        Assert.Equal("chat-model", payload.Model);
        Assert.Equal("prompt", payload.Prompt);
        Assert.Equal("system", payload.System);
        Assert.Empty(payload.ImagesBase64);
        Assert.Equal(request.JobId, roundTrip.JobId);
        Assert.Equal(request.DeduplicationKey, roundTrip.DeduplicationKey);
        Assert.Equal(request.Priority, roundTrip.Priority);
        Assert.Equal(request.CreatedAtUtc, roundTrip.CreatedAtUtc);
    }

    [Theory]
    [InlineData(typeof(LocalJobKind))]
    [InlineData(typeof(LocalJobPriority))]
    [InlineData(typeof(LocalJobState))]
    public void Enums_reject_numeric_json(Type enumType)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("0", enumType));
    }

    [Fact]
    public void Direct_construction_normalizes_created_at_to_utc()
    {
        var source = new DateTimeOffset(2026, 7, 28, 12, 30, 0, TimeSpan.FromHours(3));

        var request = new LocalJobRequest(
            Guid.NewGuid(),
            "dedupe-1",
            LocalJobPriority.Interactive,
            new ListModelsJobPayload(),
            source);

        Assert.Equal(source.UtcDateTime, request.CreatedAtUtc.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, request.CreatedAtUtc.Offset);
    }

    [Theory]
    [InlineData(null, "generation", "tree")]
    [InlineData("repository", "", "tree")]
    [InlineData("repository", "generation", " ")]
    public void IndexContext_rejects_blank_values(
        string? repositoryId,
        string? generationId,
        string? gitTree)
    {
        Assert.ThrowsAny<ArgumentException>(() => new IndexContext(
            repositoryId!,
            generationId!,
            gitTree!));
    }

    [Fact]
    public void IndexContext_preserves_valid_values()
    {
        var context = new IndexContext("repository", "generation", "tree");

        Assert.Equal("repository", context.RepositoryId);
        Assert.Equal("generation", context.GenerationId);
        Assert.Equal("tree", context.GitTree);
    }

    [Fact]
    public void Result_carries_value_and_usage_receipt()
    {
        var jobId = Guid.NewGuid();
        var receipt = new LocalUsageReceipt(
            jobId,
            "code-search",
            "embed",
            "embedding-model",
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            42,
            11,
            "repository",
            "generation",
            "tree");

        var result = new LocalJobResult<string>("value", receipt);

        Assert.Equal("value", result.Value);
        Assert.Same(receipt, result.Receipt);
        Assert.Equal(jobId, result.Receipt.JobId);
    }

    private static void AssertJsonStringRoundTrip<T>(T value)
        where T : struct, Enum
    {
        var json = JsonSerializer.Serialize(value);

        Assert.Equal($"\"{value}\"", json);
        Assert.Equal(value, JsonSerializer.Deserialize<T>(json));
    }
}
