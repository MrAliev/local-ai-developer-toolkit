using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The whole network surface of update awareness, exercised without a network.
///
/// What matters here is not that the happy path works — it is that nothing else is ever
/// believed. A version is a reason shown to a person to go and install something, so it counts
/// only when it arrives inside a manifest signed by the release key; everything else is "no
/// update information", which is a silence rather than an error.
/// </summary>
public sealed class UpdateCheckProbeTests : IDisposable
{
    private static readonly BigInteger P256Order = BigInteger.Parse(
        "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",
        System.Globalization.NumberStyles.HexNumber);

    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private const string Repository = "MrAliev/local-ai-developer-toolkit";

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => key.Dispose();

    [Fact]
    public async Task A_signed_manifest_is_believed_and_named_with_where_to_read_about_it()
    {
        var manifest = Manifest("0.1.51");
        using var probe = Probe(Responses("v0.1.51", manifest, Sign(manifest)));

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Verified, state.Status);
        Assert.Equal("0.1.51", state.LatestVersion);
        Assert.Equal(Now, state.CheckedAtUtc);
        Assert.Equal(
            "https://github.com/" + Repository + "/releases/tag/v0.1.51",
            state.ReleaseUrl);
    }

    /// <summary>
    /// The reason the check verifies before believing: whoever answers the request must not be
    /// able to invent a version. A manifest signed by a stranger is not information.
    /// </summary>
    [Fact]
    public async Task A_manifest_signed_by_somebody_else_tells_us_nothing()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest("9.9.9");
        using var probe = new UpdateCheckProbe(
            new HttpClient(Responses("v9.9.9", manifest, Sign(manifest))),
            Repository,
            ownsClient: true,
            trustedPublicKey: stranger.ExportSubjectPublicKeyInfo());

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Unavailable, state.Status);
        Assert.Null(state.LatestVersion);
    }

    [Fact]
    public async Task A_tampered_manifest_tells_us_nothing()
    {
        var manifest = Manifest("0.1.51");
        var signature = Sign(manifest);
        var tampered = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(manifest).Replace("0.1.51", "9.9.9", StringComparison.Ordinal));
        using var probe = Probe(Responses("v0.1.51", tampered, signature));

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Unavailable, state.Status);
    }

    [Fact]
    public async Task A_network_that_is_not_there_is_an_answer_not_a_failure()
    {
        using var probe = Probe(new ThrowingHandler(new HttpRequestException("no route")));

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Unavailable, state.Status);
        Assert.Equal(Now, state.CheckedAtUtc);
    }

    [Fact]
    public async Task A_release_page_that_names_no_tag_ends_the_check_quietly()
    {
        var handler = new RoutingHandler();
        handler.Map(
            ReleaseAssets.LatestRelease(Repository),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        handler.Map(
            ReleaseAssets.LatestReleaseApi(Repository),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var probe = Probe(handler);

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Unavailable, state.Status);
    }

    /// <summary>
    /// The redirect answers without spending API quota, which is shared by everyone behind one
    /// address. The API is only asked when the redirect said nothing.
    /// </summary>
    [Fact]
    public async Task The_api_is_only_asked_when_the_redirect_answers_nothing()
    {
        var manifest = Manifest("0.1.51");
        var handler = Responses("v0.1.51", manifest, Sign(manifest));
        using var probe = Probe(handler);

        await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            handler.Requested,
            uri => uri.Host == "api.github.com");
    }

    [Fact]
    public async Task The_api_answers_when_the_redirect_does_not()
    {
        var manifest = Manifest("0.1.51");
        var handler = Responses("v0.1.51", manifest, Sign(manifest));
        handler.Map(
            ReleaseAssets.LatestRelease(Repository),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        handler.Map(ReleaseAssets.LatestReleaseApi(Repository), _ => Json("""
            {"tag_name":"v0.1.51"}
            """));
        using var probe = Probe(handler);

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Verified, state.Status);
        Assert.Contains(handler.Requested, uri => uri.Host == "api.github.com");
    }

    /// <summary>
    /// Release assets redirect to a storage host. Every hop has to stay HTTPS: a check that
    /// followed a redirect to plain HTTP would be reading a document anybody on the path could
    /// have written, and the signature check is not an excuse to be careless before it.
    /// </summary>
    [Fact]
    public async Task A_redirect_off_https_is_not_followed()
    {
        var handler = new RoutingHandler();
        handler.Map(ReleaseAssets.LatestRelease(Repository), _ => Redirect(
            "https://github.com/" + Repository + "/releases/tag/v0.1.51"));
        handler.Map(
            ReleaseAssets.Asset("v0.1.51", ReleaseAssets.ManifestAsset, Repository),
            _ => Redirect("http://storage.example.invalid/manifest.json"));
        using var probe = Probe(handler);

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Unavailable, state.Status);
        Assert.DoesNotContain(
            handler.Requested,
            uri => uri.Scheme == Uri.UriSchemeHttp);
    }

    [Fact]
    public async Task A_response_that_keeps_coming_is_not_a_manifest()
    {
        var handler = new RoutingHandler();
        handler.Map(ReleaseAssets.LatestRelease(Repository), _ => Redirect(
            "https://github.com/" + Repository + "/releases/tag/v0.1.51"));
        handler.Map(
            ReleaseAssets.Asset("v0.1.51", ReleaseAssets.ManifestAsset, Repository),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[128 * 1024]),
            });
        using var probe = Probe(handler);

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Unavailable, state.Status);
    }

    /// <summary>
    /// The same ceiling, for a response that declares no length. A server that streams forever
    /// is the case the header check cannot catch, and it is the one worth catching: the broker
    /// is supposed to be doing something else.
    /// </summary>
    [Fact]
    public async Task A_response_without_a_declared_length_is_still_bounded()
    {
        var handler = new RoutingHandler();
        handler.Map(ReleaseAssets.LatestRelease(Repository), _ => Redirect(
            "https://github.com/" + Repository + "/releases/tag/v0.1.51"));
        handler.Map(
            ReleaseAssets.Asset("v0.1.51", ReleaseAssets.ManifestAsset, Repository),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EndlessStream()),
            });
        using var probe = Probe(handler);

        var state = await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Unavailable, state.Status);
    }

    /// <summary>
    /// Nothing about this machine is sent. The user agent names the program and stops there —
    /// no version, no installation id, no account — because the promise the opt-in makes is
    /// that the request says nothing about who is asking.
    /// </summary>
    [Fact]
    public async Task The_request_carries_no_identifier_of_any_kind()
    {
        var manifest = Manifest("0.1.51");
        var handler = Responses("v0.1.51", manifest, Sign(manifest));
        using var probe = Probe(handler);

        await probe.CheckAsync(Now, TestContext.Current.CancellationToken);

        Assert.NotEmpty(handler.Headers);
        Assert.All(handler.Headers, headers =>
        {
            Assert.Equal("LocalAi/1", string.Join(" ", headers.UserAgent));
            Assert.Null(headers.Authorization);
            Assert.DoesNotContain(
                headers,
                header => header.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task Cancelling_the_check_is_not_reported_as_a_missing_update()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var probe = Probe(new ThrowingHandler(new HttpRequestException("unused")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.CheckAsync(Now, cancellation.Token));
    }

    private static UpdateCheckProbe Probe(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Repository, ownsClient: true, trustedPublicKey: null);

    private UpdateCheckProbe Probe(RoutingHandler handler) =>
        new(
            new HttpClient(handler),
            Repository,
            ownsClient: true,
            trustedPublicKey: key.ExportSubjectPublicKeyInfo());

    /// <summary>The three requests a successful check makes, answered as GitHub answers them.</summary>
    private static RoutingHandler Responses(string tag, byte[] manifest, byte[] signature)
    {
        var handler = new RoutingHandler();
        handler.Map(ReleaseAssets.LatestRelease(Repository), _ => Redirect(
            "https://github.com/" + Repository + "/releases/tag/" + tag));
        handler.Map(
            ReleaseAssets.Asset(tag, ReleaseAssets.ManifestAsset, Repository),
            _ => Bytes(manifest));
        handler.Map(
            ReleaseAssets.Asset(tag, ReleaseAssets.SignatureAsset, Repository),
            _ => Bytes(signature));
        return handler;
    }

    private static byte[] Manifest(string version) =>
        ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(new ReleaseManifest(
            1,
            version,
            version,
            "signed-7",
            BrokerCompatibilityContract.ProtocolVersion,
            BrokerCompatibilityContract.BuildCompatibilityId,
            new Uri("https://releases.example.invalid/localai-" + version + ".zip"),
            1024,
            Convert.ToHexString(SHA256.HashData([1, 2, 3])),
            false,
            []));

    /// <summary>
    /// Low-S normalised, because the verifier refuses the other half of every signature pair:
    /// two encodings of one signature is a malleability the release format does not allow.
    /// </summary>
    private byte[] Sign(byte[] payload)
    {
        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var s = new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
        if (s > P256Order / 2)
        {
            var normalised = (P256Order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
            var destination = signature.AsSpan(32);
            destination.Clear();
            normalised.CopyTo(destination[(32 - normalised.Length)..]);
        }

        return signature;
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Bytes(byte[] content) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes =
            new(StringComparer.OrdinalIgnoreCase);

        public List<Uri> Requested { get; } = [];

        public List<System.Net.Http.Headers.HttpRequestHeaders> Headers { get; } = [];

        public void Map(Uri uri, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            routes[uri.ToString()] = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            Headers.Add(request.Headers);
            return Task.FromResult(
                routes.TryGetValue(request.RequestUri!.ToString(), out var respond)
                    ? respond(request)
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>A response body that never ends, and never says how long it is.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)'{');
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
    }
}
