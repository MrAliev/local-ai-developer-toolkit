using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// Reads a release from a public repository over plain HTTPS, with no account of any kind.
///
/// This is the path a first-time installation takes. Requiring `gh auth login` — and before
/// that a GitHub account, and before that an invitation — to install a published tool is
/// three obstacles in front of a download that anyone is allowed to make. The GitHub CLI
/// remains as a fallback for a repository that is not public, which is what
/// <see cref="FallbackReleaseFeed"/> is for.
///
/// Trust does not come from the transport. The manifest is verified against the key embedded
/// in this assembly before anything is fetched on its authority, and the package is checked
/// against the hash inside that manifest afterwards. An anonymous download of a signed
/// document is exactly as trustworthy as an authenticated one; what it is not is exactly as
/// inconvenient.
///
/// Redirects are followed by hand rather than by <see cref="HttpClient"/>, because a release
/// asset always redirects to a storage host and each hop still has to be HTTPS. Following
/// them here keeps that check ours instead of the handler's.
/// </summary>
public sealed class AnonymousReleaseFeed : IReleaseFeed, IDisposable
{
    private const int MaximumRedirects = 5;
    private const long MaximumDocumentBytes = 64 * 1024;
    private const long MaximumSignatureBytes = 4 * 1024;

    /// <summary>
    /// The manifest verifier's own ceiling for a package, so a hostile or broken response
    /// cannot fill a disk before anything gets a chance to reject it.
    /// </summary>
    private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;

    private static readonly TimeSpan DocumentTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly string repository;

    public AnonymousReleaseFeed(string? repository = null)
        : this(
            new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.None,
                },
                disposeHandler: true),
            repository,
            ownsClient: true)
    {
    }

    public AnonymousReleaseFeed(
        HttpClient client,
        string? repository = null,
        bool ownsClient = false)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.ownsClient = ownsClient;
        this.repository = string.IsNullOrWhiteSpace(repository)
            ? GitHubReleaseFeed.DefaultRepository
            : repository;
        if (!this.client.DefaultRequestHeaders.UserAgent.Any())
        {
            // GitHub answers 403 to an API request without one, and a request that says who
            // is asking is easier to explain in someone's proxy log than one that does not.
            this.client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("LocalAi-Installer", "1"));
        }
    }

    public async Task<string> ResolveTagAsync(
        string requestedTag,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(requestedTag) &&
            !string.Equals(requestedTag.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
        {
            return requestedTag.Trim();
        }

        // The web redirect first, because it costs no API quota: /releases/latest answers 302
        // to /releases/tag/<tag>. Anonymous API calls are limited to sixty an hour per address,
        // and an installer that spends one of them on something a redirect already answers is
        // an installer that fails on a shared network for no reason.
        var redirected = await TryResolveTagByRedirectAsync(cancellationToken)
            .ConfigureAwait(false);
        if (redirected is not null)
        {
            return redirected;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var api = await TryResolveTagByApiAsync(cancellationToken).ConfigureAwait(false);
        return api ?? throw new ReleaseResolutionException(
            "Could not determine the newest release of " + repository +
            ". Check this computer's internet connection, then try again.");
    }

    public async Task<ResolvedRelease> ResolveAsync(
        string tag,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Directory.CreateDirectory(workingDirectory);

        var manifestJson = await DownloadAssetAsync(
                tag,
                GitHubReleaseFeed.ManifestAsset,
                Path.Combine(workingDirectory, GitHubReleaseFeed.ManifestAsset),
                MaximumDocumentBytes,
                DocumentTimeout,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        var signature = await DownloadAssetAsync(
                tag,
                GitHubReleaseFeed.SignatureAsset,
                Path.Combine(workingDirectory, GitHubReleaseFeed.SignatureAsset),
                MaximumSignatureBytes,
                DocumentTimeout,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var verifier = ReleaseTrustAnchor.CreateManifestVerifier();
            return new ResolvedRelease(
                verifier.Verify(manifestJson, signature),
                manifestJson,
                signature);
        }
        catch (ReleaseVerificationException exception)
        {
            throw new ReleaseResolutionException(
                $"The manifest for release '{tag}' failed verification and will not be used.",
                exception);
        }
    }

    public async Task<string> DownloadPackageAsync(
        string tag,
        string workingDirectory,
        IProgress<long>? bytesDownloaded = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Directory.CreateDirectory(workingDirectory);
        var target = Path.Combine(workingDirectory, GitHubReleaseFeed.PackageAsset);
        await DownloadAssetAsync(
                tag,
                GitHubReleaseFeed.PackageAsset,
                target,
                MaximumPackageBytes,
                PackageTimeout,
                bytesDownloaded,
                cancellationToken)
            .ConfigureAwait(false);
        return target;
    }

    internal Uri AssetUri(string tag, string assetName) =>
        new($"https://github.com/{repository}/releases/download/" +
            $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}");

    private async Task<string?> TryResolveTagByRedirectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"https://github.com/{repository}/releases/latest"));
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.Headers.Location is not { } location)
            {
                return null;
            }

            var absolute = location.IsAbsoluteUri
                ? location
                : new Uri(new Uri("https://github.com/"), location);
            var segments = absolute.AbsolutePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            var index = Array.LastIndexOf(segments, "tag");
            return index >= 0 && index + 1 < segments.Length
                ? Uri.UnescapeDataString(segments[index + 1])
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private async Task<string?> TryResolveTagByApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"https://api.github.com/repos/{repository}/releases/latest"));
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false));
            return document.RootElement.TryGetProperty("tag_name", out var tag)
                ? tag.GetString()
                : null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches one asset to a file, following redirects by hand and refusing anything that
    /// grows past what the caller allowed. Returns the bytes for the small documents; the
    /// package is written to disk and the return value ignored.
    /// </summary>
    private async Task<byte[]> DownloadAssetAsync(
        string tag,
        string assetName,
        string targetPath,
        long maximumBytes,
        TimeSpan timeout,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var uri = AssetUri(tag, assetName);

        try
        {
            for (var hop = 0; hop <= MaximumRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.AcceptEncoding.Clear();
                var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                try
                {
                    if (response.Headers.Location is { } location)
                    {
                        var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        if (!string.Equals(
                                next.Scheme,
                                Uri.UriSchemeHttps,
                                StringComparison.Ordinal))
                        {
                            throw new ReleaseResolutionException(
                                $"The download of '{assetName}' was redirected away from " +
                                "HTTPS and was abandoned.");
                        }

                        uri = next;
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new ReleaseResolutionException(
                            $"Release '{tag}' has no asset named '{assetName}'. Check the " +
                            "release tag.");
                    }

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new ReleaseResolutionException(
                            $"Could not download '{assetName}' from release '{tag}': the " +
                            $"server answered {(int)response.StatusCode} " +
                            $"{response.ReasonPhrase}.");
                    }

                    if (response.Content.Headers.ContentLength is { } declared &&
                        declared > maximumBytes)
                    {
                        throw new ReleaseResolutionException(
                            $"'{assetName}' is larger than this installer will download " +
                            $"({declared} bytes).");
                    }

                    return await CopyAsync(
                            response,
                            targetPath,
                            maximumBytes,
                            assetName,
                            progress,
                            token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    response.Dispose();
                }
            }

            throw new ReleaseResolutionException(
                $"The download of '{assetName}' was redirected too many times.");
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseResolutionException(
                $"The download of '{assetName}' from release '{tag}' timed out.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or
                UnauthorizedAccessException or NotSupportedException)
        {
            throw new ReleaseResolutionException(
                $"Could not download '{assetName}' from release '{tag}': {exception.Message}",
                exception);
        }
    }

    private static async Task<byte[]> CopyAsync(
        HttpResponseMessage response,
        string targetPath,
        long maximumBytes,
        string assetName,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        // The manifest and the signature are wanted as bytes, and they are kept as the
        // transfer runs rather than read back afterwards. Reading the file back is what the
        // first version did, and it cannot work: the stream above still holds the file with
        // no sharing, so every document download failed with "used by another process". The
        // package is gigabytes and its caller wants the path, so it is never captured.
        var capture = maximumBytes <= MaximumDocumentBytes ? new MemoryStream() : null;
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false))
               > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new ReleaseResolutionException(
                    $"'{assetName}' is larger than this installer will download.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            capture?.Write(buffer, 0, read);
            progress?.Report(total);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return capture?.ToArray() ?? [];
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }
}
