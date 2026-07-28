using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Cli;

public static class NativeCommand
{
    public static async Task<JsonElement> ExecuteAsync(
        string operation,
        string? requestPath,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<NativeOllamaOperation>(
                operation,
                ignoreCase: true,
                out var parsedOperation))
        {
            throw new ArgumentException(
                $"Unsupported native Ollama operation '{operation}'.",
                nameof(operation));
        }

        JsonElement? body = null;
        if (!string.IsNullOrWhiteSpace(requestPath))
        {
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(requestPath, cancellationToken));
            body = document.RootElement.Clone();
        }

        var canonical = body?.GetRawText() ?? string.Empty;
        var deduplicationKey = "native:" + parsedOperation + ":" +
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var request = LocalJobRequestFactory.CreateNativeOllama(
            deduplicationKey,
            LocalJobPriority.Foreground,
            parsedOperation,
            body);
        var result = await BrokerClientFactory.CreateDefault()
            .ExecuteAsync<NativeOllamaJobOutput>(request, cancellationToken);
        return result.Value.Response;
    }
}
