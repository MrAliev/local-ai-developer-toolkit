using System.Net;
using System.Text;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The path a first installation takes: a public release, read with no account at all.
///
/// Requiring `gh auth login` — and before it a GitHub account, and before that an invitation —
/// to install a published tool was three obstacles in front of a download anyone is allowed to
/// make. What replaces them is plain HTTPS, and what does not change is that nothing is
/// believed because of where it came from: the manifest is still checked against the embedded
/// key, and the package against the hash inside it.
/// </summary>
public sealed class AnonymousReleaseFeedTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Anonymous.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Resolves_latest_from_the_redirect_rather_than_the_api()
    {
        var handler = new ScriptedHandler();
        handler.Redirect(
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/latest",
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/tag/0.1.45");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var tag = await feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
        // The anonymous API allows sixty calls an hour per address, and an installer that
        // spends one where a redirect already answers is one that fails on a shared network.
        Assert.DoesNotContain(
            handler.Requests,
            uri => uri.Host.Equals("api.github.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Falls_back_to_the_api_when_the_redirect_says_nothing()
    {
        var handler = new ScriptedHandler();
        handler.Respond(
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/latest",
            HttpStatusCode.OK,
            "<html>a page, not a redirect</html>");
        handler.Respond(
            "https://api.github.com/repos/MrAliev/local-ai-developer-toolkit/releases/latest",
            HttpStatusCode.OK,
            """{"tag_name":"0.1.45"}""");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var tag = await feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
    }

    [Fact]
    public async Task An_explicit_tag_is_never_looked_up()
    {
        var handler = new ScriptedHandler();
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var tag = await feed.ResolveTagAsync("0.1.44", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.44", tag);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Downloads_a_package_through_a_redirect_and_reports_progress()
    {
        var payload = Encoding.UTF8.GetBytes(new string('p', 40_000));
        var handler = new ScriptedHandler();
        handler.Redirect(
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/0.1.45/localai-package.zip",
            "https://objects.githubusercontent.com/package");
        handler.RespondBytes("https://objects.githubusercontent.com/package", payload);
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));
        var seen = new List<long>();

        var path = await feed.DownloadPackageAsync(
            "0.1.45",
            root,
            new Progress<long>(seen.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(
            path,
            TestContext.Current.CancellationToken));
        // Progress is observed from the transfer itself rather than polled off a growing
        // file, so the last report is the real total rather than whatever the poll caught.
        Assert.Equal(payload.Length, seen.LastOrDefault());
    }

    /// <summary>
    /// A redirect off HTTPS is refused rather than followed. The signature would catch a
    /// substituted manifest anyway, but a download that silently leaves TLS is worth refusing
    /// before the bytes arrive, not after.
    /// </summary>
    [Fact]
    public async Task Refuses_a_redirect_that_leaves_https()
    {
        var handler = new ScriptedHandler();
        handler.Redirect(
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/0.1.45/localai-package.zip",
            "http://mirror.invalid/package");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.DownloadPackageAsync(
                "0.1.45",
                root,
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_missing_asset_by_name()
    {
        var handler = new ScriptedHandler();
        handler.Respond(
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/9.9.9/localai-package.zip",
            HttpStatusCode.NotFound,
            "Not Found");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.DownloadPackageAsync(
                "9.9.9",
                root,
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("9.9.9", exception.Message, StringComparison.Ordinal);
        Assert.Contains("localai-package.zip", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest that fails the embedded key is refused, whatever transport carried it. This
    /// is the property that makes an anonymous download acceptable in the first place.
    /// </summary>
    [Fact]
    public async Task Refuses_a_manifest_that_fails_verification()
    {
        var handler = new ScriptedHandler();
        handler.Respond(
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/0.1.45/release-manifest.json",
            HttpStatusCode.OK,
            """{"SchemaVersion":1,"ReleaseVersion":"0.1.45"}""");
        handler.RespondBytes(
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/0.1.45/release-manifest.sig",
            new byte[64]);
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.45", root, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> answers =
            new(StringComparer.OrdinalIgnoreCase);

        public List<Uri> Requests { get; } = [];

        public void Redirect(string from, string to) =>
            answers[from] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(to);
                return response;
            };

        public void Respond(string uri, HttpStatusCode status, string body) =>
            answers[uri] = () => new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };

        public void RespondBytes(string uri, byte[] body) =>
            answers[uri] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return Task.FromResult(
                answers.TryGetValue(uri.AbsoluteUri, out var answer)
                    ? answer()
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("Not Found"),
                    });
        }
    }
}
