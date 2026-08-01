using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalAi.Installer.Core.Models;

public interface IModelSizeSource
{
    Task<long?> GetDownloadSizeBytesAsync(string tag, CancellationToken cancellationToken);
}

/// <summary>
/// Reads model sizes from the public Ollama registry.
///
/// The routing catalogue knows which models exist and how they may be used, but not how big
/// they are, and a size baked into the product goes stale silently the moment a model is
/// republished with different quantisation. So the size is fetched, and only the size.
///
/// This talks to the public registry for metadata only. It never downloads a model and never
/// contacts a local Ollama daemon — pulling a model still goes through the broker.
/// </summary>
public sealed class OllamaRegistryClient : IModelSizeSource, IDisposable
{
    private static readonly Uri DefaultRegistry = new("https://registry.ollama.ai/");

    private static readonly MediaTypeWithQualityHeaderValue ManifestMediaType =
        new("application/vnd.docker.distribution.manifest.v2+json");

    private readonly HttpClient client;
    private readonly bool ownsClient;

    public OllamaRegistryClient(TimeSpan? timeout = null)
        : this(new HttpClient { BaseAddress = DefaultRegistry }, ownsClient: true)
    {
        client.Timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public OllamaRegistryClient(HttpClient client, bool ownsClient = false)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.ownsClient = ownsClient;
        if (this.client.BaseAddress is null)
        {
            this.client.BaseAddress = DefaultRegistry;
        }
    }

    /// <summary>
    /// Total size of the manifest's layers, or null when the size could not be established.
    /// Null means "unknown", never "zero": an installer that cannot reach the network must
    /// say it does not know rather than imply the model is free.
    /// </summary>
    public async Task<long?> GetDownloadSizeBytesAsync(
        string tag,
        CancellationToken cancellationToken)
    {
        if (!TrySplit(tag, out var name, out var version))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"v2/library/{name}/manifests/{version}");
            request.Headers.Accept.Add(ManifestMediaType);

            using var response = await client.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("layers", out var layers) ||
                layers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            long total = 0;
            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.TryGetProperty("size", out var size) &&
                    size.TryGetInt64(out var bytes) &&
                    bytes > 0)
                {
                    total += bytes;
                }
            }

            return total > 0 ? total : null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or TaskCanceledException
                or InvalidOperationException or UriFormatException)
        {
            // Being offline is an ordinary condition for an installer, not a failure.
            return null;
        }
    }

    private static bool TrySplit(string tag, out string name, out string version)
    {
        name = string.Empty;
        version = "latest";
        if (string.IsNullOrWhiteSpace(tag) || tag.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var separator = tag.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            name = tag;
            return true;
        }

        if (separator == 0 || separator == tag.Length - 1)
        {
            return false;
        }

        name = tag[..separator];
        version = tag[(separator + 1)..];
        return true;
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }
}
