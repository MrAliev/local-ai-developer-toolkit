using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public static class ReceiptFactory
{
    public static LocalUsageReceipt Create(
        LocalJobRequest request,
        DateTimeOffset executionStartedAtUtc,
        DateTimeOffset executionCompletedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputCharacters = InputCharacters(request.Payload);
        return new LocalUsageReceipt(
            request.JobId,
            Tool(request.Kind),
            Operation(request.Kind),
            Model(request.Payload),
            NonNegative(executionStartedAtUtc - request.CreatedAtUtc),
            NonNegative(executionCompletedAtUtc - executionStartedAtUtc),
            inputCharacters,
            (inputCharacters + 3) / 4,
            null,
            null,
            null);
    }

    private static string Tool(LocalJobKind kind) =>
        kind == LocalJobKind.Embed ? "code-search" : "local-lm";

    private static string Operation(LocalJobKind kind) =>
        kind switch
        {
            LocalJobKind.Embed => "embed",
            LocalJobKind.Chat => "chat",
            LocalJobKind.ListModels => "list-models",
            LocalJobKind.NativeOllama => "native-ollama",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string Model(LocalJobPayload payload) =>
        payload switch
        {
            EmbedJobPayload embed => embed.Model,
            ChatJobPayload chat => chat.Model,
            ListModelsJobPayload => "n/a",
            NativeOllamaJobPayload native => NativeModel(native.RequestBody),
            _ => throw new ArgumentOutOfRangeException(nameof(payload))
        };

    private static long InputCharacters(LocalJobPayload payload) =>
        payload switch
        {
            EmbedJobPayload embed => embed.Inputs.Sum(value => (long)value.Length),
            ChatJobPayload chat =>
                chat.Prompt.Length +
                (chat.System?.Length ?? 0) +
                chat.ImagesBase64.Sum(value => (long)value.Length),
            ListModelsJobPayload => 0,
            NativeOllamaJobPayload native =>
                native.RequestBody?.GetRawText().Length ?? 0,
            _ => throw new ArgumentOutOfRangeException(nameof(payload))
        };

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string NativeModel(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("model", out var model) &&
        model.ValueKind == JsonValueKind.String
            ? model.GetString() ?? "n/a"
            : "n/a";
}
