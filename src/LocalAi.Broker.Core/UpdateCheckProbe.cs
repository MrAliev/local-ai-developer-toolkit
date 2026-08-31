using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// Asks, once, whether a newer release exists — and believes the answer only if it is signed.
///
/// This is the whole network surface of update awareness, and it is deliberately small: two
/// documents, both public, both tiny, fetched with no credential and no identifier. Nothing
/// about this machine is sent. What comes back is a manifest and a detached signature, and the
/// version inside the manifest is believed only after the signature verifies against the key
/// embedded in this build. Whoever answers the request therefore cannot invent a version, which
/// matters because the answer is shown to a person as a reason to go and install something.
///
/// Every failure — no network, a proxy that rewrites the body, a signature that does not verify
/// — produces the same thing: no update information. Not an error, not a retry, and above all
/// not a banner urging action. The caller records "unavailable" and asks again after the
/// interval, which is what stops a broken network from becoming a request per minute.
/// </summary>
public sealed class UpdateCheckProbe : IDisposable
{
    /// <summary>
    /// A manifest is a few kilobytes and a signature is under a hundred bytes. The ceilings are
    /// here so a hostile or broken response cannot stream forever into a broker that is
    /// supposed to be doing something else.
    /// </summary>
    private const int MaximumManifestBytes = 64 * 1024;

    private const int MaximumSignatureBytes = 4 * 1024;

    private const int MaximumRedirects = 5;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly string? repository;
    private readonly byte[]? trustedPublicKey;

    public UpdateCheckProbe(string? repository = null)
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

    /// <summary>
    /// <paramref name="trustedPublicKey"/> exists for tests, which cannot sign anything with
    /// the real release key and would otherwise be able to exercise only the refusals. Left
    /// null — as every shipping caller leaves it — the key embedded in this build is used, so
    /// the trust anchor is not something a configuration file can point elsewhere.
    /// </summary>
    public UpdateCheckProbe(
        HttpClient client,
        string? repository = null,
        bool ownsClient = false,
        byte[]? trustedPublicKey = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.ownsClient = ownsClient;
        this.repository = repository;
        this.trustedPublicKey = trustedPublicKey?.ToArray();
        if (!this.client.DefaultRequestHeaders.UserAgent.Any())
        {
            // Says which program is asking and nothing else: no version, no machine, no
            // installation id. GitHub refuses an API request without any user agent at all,
            // and a request that names the program is easier to account for in somebody's
            // proxy log than one that does not.
            this.client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("LocalAi", "1"));
        }
    }

    /// <summary>
    /// One check. Returns what to record: a verified version, or the fact that there is
    /// nothing to believe right now. Never throws for a network or verification problem —
    /// those are answers, and an update check that can fail a broker is worse than no update
    /// check.
    /// </summary>
    public async Task<UpdateCheckState> CheckAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tag = await ResolveLatestTagAsync(cancellationToken).ConfigureAwait(false);
            if (tag is null)
            {
                return Unavailable(now);
            }

            var manifestJson = await DownloadAsync(
                    ReleaseAssets.Asset(tag, ReleaseAssets.ManifestAsset, repository),
                    MaximumManifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var signature = await DownloadAsync(
                    ReleaseAssets.Asset(tag, ReleaseAssets.SignatureAsset, repository),
                    MaximumSignatureBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (manifestJson is null || signature is null)
            {
                return Unavailable(now);
            }

            using var verifier = trustedPublicKey is null
                ? ReleaseTrustAnchor.CreateManifestVerifier()
                : new ReleaseManifestVerifier(trustedPublicKey);
            var manifest = verifier.Verify(manifestJson, signature);
            return new UpdateCheckState(
                1,
                UpdateCheckStatus.Verified,
                now,
                manifest.ReleaseVersion,
                ReleaseAssets.Release(tag, repository).ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout, which is a network answer rather than somebody asking to stop.
            return Unavailable(now);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or
                ReleaseVerificationException or InvalidOperationException or UriFormatException)
        {
            return Unavailable(now);
        }
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    private static UpdateCheckState Unavailable(DateTimeOffset now) =>
        new(1, UpdateCheckStatus.Unavailable, now, null, null);

    /// <summary>
    /// Which release is newest, from the redirect that answers it without spending API quota.
    /// The API is the fallback, because sixty anonymous calls an hour are shared by everyone
    /// behind one address and a check that burns one where a redirect would do is a check that
    /// breaks on an office network for no reason.
    /// </summary>
    private async Task<string?> ResolveLatestTagAsync(CancellationToken cancellationToken)
    {
        var redirected = await TryTagByRedirectAsync(cancellationToken).ConfigureAwait(false);
        return redirected ?? await TryTagByApiAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryTagByRedirectAsync(CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ReleaseAssets.LatestRelease(repository));
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.Headers.Location is not { } location)
        {
            return null;
        }

        var absolute = location.IsAbsoluteUri
            ? location
            : new Uri(new Uri("https://github.com/"), location);
        var segments = absolute.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.LastIndexOf(segments, "tag");
        return index >= 0 && index + 1 < segments.Length
            ? Uri.UnescapeDataString(segments[index + 1])
            : null;
    }

    private async Task<string?> TryTagByApiAsync(CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ReleaseAssets.LatestReleaseApi(repository));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("tag_name", out var tag)
            ? tag.GetString()
            : null;
    }

    /// <summary>
    /// Fetches one small document, following redirects by hand so every hop stays HTTPS — a
    /// release asset always redirects to a storage host, and a redirect to anything else is
    /// not a hop this will take.
    /// </summary>
    private async Task<byte[]?> DownloadAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var current = uri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var timeout = Timeout(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Moved or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or
                HttpStatusCode.SeeOther)
            {
                if (response.Headers.Location is not { } location)
                {
                    return null;
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
                {
                    return null;
                }

                current = next;
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength > maximumBytes)
            {
                return null;
            }

            using var content = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8 * 1024];
            int read;
            while ((read = await content.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maximumBytes)
                {
                    // A response that keeps coming is not a manifest, whatever it claims.
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        return null;
    }

    private CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(RequestTimeout);
        return source;
    }
}
