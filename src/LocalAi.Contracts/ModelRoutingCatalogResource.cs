using System.Reflection;
using System.Text.Json;

namespace LocalAi.Contracts;

/// <summary>
/// Owns the embedded routing catalog document.
///
/// The catalog is the only place that knows which models exist, what each one can do and
/// which context sizes it may be loaded with — none of that is published by the model
/// registry. The broker needs it to route requests; the installer needs it to offer a choice
/// that the broker will actually accept. Both read it from here so there is one copy, and
/// validation stays where routing lives.
/// </summary>
public static class ModelRoutingCatalogResource
{
    public const string ResourceName = "LocalAi.model-routing.json";

    public static ModelRoutingCatalogDocument LoadDocument()
    {
        using var stream = typeof(ModelRoutingCatalogResource).Assembly
                .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded {ResourceName} was not found.");

        return JsonSerializer.Deserialize<ModelRoutingCatalogDocument>(
                stream,
                LocalAiJson.Strict)
            ?? throw new InvalidDataException("Model routing catalog is empty.");
    }

    /// <summary>
    /// Models that can be offered to a user: every entry the catalog declares with at least
    /// one capability and one usable context size. Callers that only need to present a choice
    /// use this instead of taking a dependency on the broker.
    /// </summary>
    public static IReadOnlyList<ModelCatalogEntry> SelectableModels()
    {
        try
        {
            return
            [
                .. LoadDocument().Models
                    .Where(model =>
                        !string.IsNullOrWhiteSpace(model.Tag) &&
                        model.Capabilities is { Count: > 0 } &&
                        model.ContextTokens is { Count: > 0 })
                    .OrderBy(model => model.Tag, StringComparer.Ordinal)
            ];
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or InvalidOperationException)
        {
            // A caller that only wants to show a list must not fail to start because of a
            // malformed catalog; the broker still validates it properly on startup.
            return [];
        }
    }
}
