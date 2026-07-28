using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerPayloadContractTests
{
    public static TheoryData<Type> PayloadTypes =>
        new(
            typeof(EmbedJobPayload),
            typeof(ChatJobPayload),
            typeof(ListModelsJobPayload));

    [Fact]
    public void Strict_json_options_are_read_only_and_reject_duplicate_root_request_fields()
    {
        Assert.True(LocalAiJson.Strict.IsReadOnly);
        Assert.Throws<InvalidOperationException>(
            () => LocalAiJson.Strict.AllowDuplicateProperties = true);

        foreach (var json in new[]
                 {
                     """
                     {
                       "JobId": "9cf83dbc-fdd1-4ea9-be4f-bd18c191e63f",
                       "DeduplicationKey": "dedupe",
                       "Priority": "Foreground",
                       "Priority": "Background",
                       "Payload": { "$type": "listModels" },
                       "CreatedAtUtc": "2026-07-28T09:30:00+00:00"
                     }
                     """,
                     """
                     {
                       "JobId": "9cf83dbc-fdd1-4ea9-be4f-bd18c191e63f",
                       "DeduplicationKey": "dedupe",
                       "Priority": "Foreground",
                       "Payload": { "$type": "listModels" },
                       "Payload": { "$type": "listModels" },
                       "CreatedAtUtc": "2026-07-28T09:30:00+00:00"
                     }
                     """
                 })
        {
            Assert.Throws<JsonException>(
                () => JsonSerializer.Deserialize<LocalJobRequest>(json, LocalAiJson.Strict));
        }
    }

    [Theory]
    [InlineData("""{"$type":"embed","$type":"embed","Model":"model","Inputs":["input"]}""")]
    [InlineData("""{"$type":"embed","Model":"first","Model":"second","Inputs":["input"]}""")]
    [InlineData("""{"$type":"chat","Model":"model","Prompt":"first","Prompt":"second","System":null,"ImagesBase64":[]}""")]
    public void Strict_json_options_reject_duplicate_payload_fields(string json)
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<LocalJobPayload>(json, LocalAiJson.Strict));
    }

    [Fact]
    public void Payload_discriminators_are_pinned_to_exact_stable_strings()
    {
        var cases = new Dictionary<LocalJobPayload, string>
        {
            [new EmbedJobPayload("model", ["input"])] = "embed",
            [new ChatJobPayload("model", "prompt", null, [])] = "chat",
            [new ListModelsJobPayload()] = "listModels"
        };

        foreach (var (payload, discriminator) in cases)
        {
            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize<LocalJobPayload>(payload, LocalAiJson.Strict));

            Assert.Equal(discriminator, document.RootElement.GetProperty("$type").GetString());
        }
    }

    [Theory]
    [MemberData(nameof(PayloadTypes))]
    public void Payload_types_are_sealed_and_have_one_public_json_constructor(Type payloadType)
    {
        Assert.True(payloadType.IsSealed);
        var constructor = Assert.Single(payloadType.GetConstructors());
        Assert.NotNull(constructor.GetCustomAttributes(typeof(JsonConstructorAttribute), false).SingleOrDefault());
    }

    [Fact]
    public void Embed_payload_round_trips_with_derived_kind_and_immutable_inputs()
    {
        var payload = new EmbedJobPayload("embed-model", ["first", "second"]);

        var roundTrip = Assert.IsType<EmbedJobPayload>(
            JsonSerializer.Deserialize<LocalJobPayload>(
                JsonSerializer.Serialize<LocalJobPayload>(payload)));

        Assert.Equal(LocalJobKind.Embed, roundTrip.Kind);
        Assert.Equal("embed-model", roundTrip.Model);
        Assert.Equal(["first", "second"], roundTrip.Inputs);
        AssertReadOnly(roundTrip.Inputs);
    }

    [Fact]
    public void Chat_payload_round_trips_with_nullable_system_and_immutable_images()
    {
        var payload = new ChatJobPayload(
            "chat-model",
            "prompt",
            null,
            ["image-one", "image-two"]);

        var roundTrip = Assert.IsType<ChatJobPayload>(
            JsonSerializer.Deserialize<LocalJobPayload>(
                JsonSerializer.Serialize<LocalJobPayload>(payload)));

        Assert.Equal(LocalJobKind.Chat, roundTrip.Kind);
        Assert.Equal("chat-model", roundTrip.Model);
        Assert.Equal("prompt", roundTrip.Prompt);
        Assert.Null(roundTrip.System);
        Assert.Equal(["image-one", "image-two"], roundTrip.ImagesBase64);
        AssertReadOnly(roundTrip.ImagesBase64);
    }

    [Fact]
    public void List_models_payload_round_trips_without_model_or_input_fields()
    {
        LocalJobPayload payload = new ListModelsJobPayload();

        var json = JsonSerializer.Serialize(payload);
        var roundTrip = Assert.IsType<ListModelsJobPayload>(
            JsonSerializer.Deserialize<LocalJobPayload>(json));

        Assert.Equal(LocalJobKind.ListModels, roundTrip.Kind);
        Assert.DoesNotContain("\"Model\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Inputs\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Prompt\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ImagesBase64\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"$type":"unknown"}""")]
    [InlineData("""{"$type":0}""")]
    [InlineData("""{}""")]
    public void Payload_json_rejects_unknown_numeric_or_missing_discriminator(string json)
    {
        var exception = Record.Exception(
            () => JsonSerializer.Deserialize<LocalJobPayload>(json));

        Assert.True(
            exception is JsonException or NotSupportedException,
            $"Expected discriminator rejection, but got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [InlineData("""{"$type":"embed","Inputs":["input"]}""")]
    [InlineData("""{"$type":"embed","Model":"model"}""")]
    [InlineData("""{"$type":"embed","Model":"model","Inputs":["input"],"Unknown":true}""")]
    [InlineData("""{"$type":"chat","Prompt":"prompt","System":null,"ImagesBase64":[]}""")]
    [InlineData("""{"$type":"chat","Model":"model","System":null,"ImagesBase64":[]}""")]
    [InlineData("""{"$type":"chat","Model":"model","Prompt":"prompt","ImagesBase64":[]}""")]
    [InlineData("""{"$type":"chat","Model":"model","Prompt":"prompt","System":null}""")]
    [InlineData("""{"$type":"listModels","Model":"model"}""")]
    public void Payload_json_rejects_wrong_missing_or_unknown_subtype_fields(string json)
    {
        var exception = Record.Exception(
            () => JsonSerializer.Deserialize<LocalJobPayload>(json));

        Assert.True(
            exception is JsonException or ArgumentException,
            $"Expected strict subtype rejection, but got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Embed_direct_construction_rejects_blank_model(string? model)
    {
        Assert.Throws<ArgumentException>(() => new EmbedJobPayload(model, ["input"]));
    }

    [Fact]
    public void Embed_json_cannot_bypass_validation()
    {
        const string json = """{"$type":"embed","Model":"model","Inputs":[" "]}""";

        Assert.Throws<ArgumentException>(
            () => JsonSerializer.Deserialize<LocalJobPayload>(json));
    }

    [Fact]
    public void Chat_direct_construction_and_json_cannot_bypass_validation()
    {
        Assert.Throws<ArgumentException>(
            () => new ChatJobPayload("model", " ", null, []));
        Assert.Throws<ArgumentException>(
            () => JsonSerializer.Deserialize<LocalJobPayload>(
                """{"$type":"chat","Model":"model","Prompt":"prompt","System":null,"ImagesBase64":[" "]}"""));
    }

    [Fact]
    public void Payloads_snapshot_mutable_input_and_image_sources()
    {
        var inputs = new List<string> { "input" };
        var images = new List<string> { "image" };

        var embed = new EmbedJobPayload("model", inputs);
        var chat = new ChatJobPayload("model", "prompt", "", images);
        inputs[0] = "changed";
        images[0] = "changed";

        Assert.Equal("input", embed.Inputs[0]);
        Assert.Equal("image", chat.ImagesBase64[0]);
        Assert.Null(chat.System);
        AssertReadOnly(embed.Inputs);
        AssertReadOnly(chat.ImagesBase64);
    }

    [Fact]
    public void Request_json_has_payload_without_duplicate_root_kind_model_or_inputs()
    {
        var request = LocalJobRequestFactory.Create(
            "dedupe",
            LocalJobPriority.Foreground,
            new EmbedJobPayload("model", ["input"]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("Payload", out var payload));
        Assert.Equal("embed", payload.GetProperty("$type").GetString());
        Assert.False(root.TryGetProperty("Kind", out _));
        Assert.False(root.TryGetProperty("Model", out _));
        Assert.False(root.TryGetProperty("Inputs", out _));
        Assert.Equal(LocalJobKind.Embed, request.Kind);
    }

    [Fact]
    public void Request_json_rejects_missing_payload()
    {
        const string json =
            """
            {
              "JobId": "9cf83dbc-fdd1-4ea9-be4f-bd18c191e63f",
              "DeduplicationKey": "dedupe",
              "Priority": "Foreground",
              "CreatedAtUtc": "2026-07-28T09:30:00+00:00"
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LocalJobRequest>(json));
    }

    [Fact]
    public void Request_direct_construction_rejects_null_payload()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalJobRequest(
            Guid.NewGuid(),
            "dedupe",
            LocalJobPriority.Foreground,
            null!,
            DateTimeOffset.UtcNow));
    }

    private static void AssertReadOnly(IReadOnlyList<string> values)
    {
        Assert.False(values is string[]);
        Assert.False(values is List<string>);
        var mutableView = Assert.IsAssignableFrom<IList<string>>(values);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = "changed");
    }
}
