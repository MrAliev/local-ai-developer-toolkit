using System.Net;
using System.Net.Http.Headers;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class HttpReleaseClientTests
{
    [Fact]
    public async Task Rejects_non_https_before_sending()
    {
        var handler = new RecordingHandler(
            (Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new InvalidOperationException()));
        using var client = new HttpReleaseClient(handler);

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            client.OpenPackageAsync(
                new Uri("http://example.invalid/package.zip"),
                3,
                TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Rejects_redirects_without_following_them()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://other.invalid/package.zip") },
        });
        using var client = new HttpReleaseClient(handler);

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            client.OpenPackageAsync(
                new Uri("https://example.invalid/package.zip"),
                3,
                TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2L)]
    [InlineData(4L)]
    public async Task Rejects_missing_or_non_exact_content_length(long? contentLength)
    {
        var handler = new RecordingHandler((_, _) => Response([1, 2, 3], contentLength));
        using var client = new HttpReleaseClient(handler);

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            client.OpenPackageAsync(
                new Uri("https://example.invalid/package.zip"),
                3,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_content_encoding()
    {
        var response = Response([1, 2, 3], 3);
        response.Content.Headers.ContentEncoding.Add("gzip");
        var handler = new RecordingHandler((_, _) => response);
        using var client = new HttpReleaseClient(handler);

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            client.OpenPackageAsync(
                new Uri("https://example.invalid/package.zip"),
                3,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_conflicting_transfer_encoding_and_content_length()
    {
        var response = Response([1, 2, 3], 3);
        response.Headers.TransferEncodingChunked = true;
        var handler = new RecordingHandler((_, _) => response);
        using var client = new HttpReleaseClient(handler);

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            client.OpenPackageAsync(
                new Uri("https://example.invalid/package.zip"),
                3,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Returns_unbuffered_stream_and_disposes_response_with_stream()
    {
        var content = new StreamingContent([1, 2, 3]);
        content.Headers.ContentLength = 3;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var handler = new RecordingHandler((_, _) => response);
        using var client = new HttpReleaseClient(handler);

        var stream = await client.OpenPackageAsync(
            new Uri("https://example.invalid/package.zip"),
            3,
            TestContext.Current.CancellationToken);

        Assert.False(content.WasSerialized);
        var singleByte = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(singleByte, TestContext.Current.CancellationToken));
        Assert.Equal(1, singleByte[0]);
        await stream.DisposeAsync();
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task Sync_dispose_releases_response_when_inner_dispose_throws()
    {
        var primary = new IOException("primary sync dispose failure");
        var content = new DisposalFaultingContent(primary);
        content.Headers.ContentLength = 3;
        var handler = new RecordingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new HttpReleaseClient(handler);
        var stream = await client.OpenPackageAsync(
            new Uri("https://example.invalid/package.zip"),
            3,
            TestContext.Current.CancellationToken);

        var thrown = Assert.Throws<IOException>(stream.Dispose);

        Assert.Same(primary, thrown);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task Async_dispose_releases_response_when_inner_dispose_throws()
    {
        var primary = new IOException("primary async dispose failure");
        var content = new DisposalFaultingContent(primary);
        content.Headers.ContentLength = 3;
        var handler = new RecordingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new HttpReleaseClient(handler);
        var stream = await client.OpenPackageAsync(
            new Uri("https://example.invalid/package.zip"),
            3,
            TestContext.Current.CancellationToken);

        var thrown = await Assert.ThrowsAsync<IOException>(async () =>
            await stream.DisposeAsync());

        Assert.Same(primary, thrown);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task Propagates_cancellation()
    {
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var client = new HttpReleaseClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.OpenPackageAsync(
                new Uri("https://example.invalid/package.zip"),
                3,
                cancellation.Token));
    }

    [Fact]
    public async Task Sanitizes_stream_transport_failures()
    {
        var content = new FaultingContent();
        content.Headers.ContentLength = 3;
        var handler = new RecordingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new HttpReleaseClient(handler);
        await using var stream = await client.OpenPackageAsync(
            new Uri("https://example.invalid/package.zip"),
            3,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ReleaseVerificationException>(async () =>
            await stream.ReadExactlyAsync(
                new byte[1],
                TestContext.Current.CancellationToken));

        Assert.Equal("Release download failed.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Response(byte[] bytes, long? contentLength)
    {
        var content = new StreamingContent(bytes);
        content.Headers.ContentLength = contentLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send)
            : this((request, token) => Task.FromResult(send(request, token)))
        {
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return send(request, cancellationToken);
        }
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        private readonly TrackingStream stream = new(bytes);

        public bool WasSerialized { get; private set; }

        public bool WasDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream target,
            TransportContext? context)
        {
            WasSerialized = true;
            throw new InvalidOperationException("The response was buffered.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(stream);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WasDisposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false);

    private sealed class FaultingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            throw new InvalidOperationException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new FaultingStream());
    }

    private sealed class DisposalFaultingContent(Exception failure) : HttpContent
    {
        private readonly DisposalFaultingStream stream = new(failure);

        public bool WasDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream target,
            TransportContext? context) =>
            throw new InvalidOperationException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(stream);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WasDisposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class DisposalFaultingStream(Exception failure) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                throw failure;
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() =>
            ValueTask.FromException(failure);
    }

    private sealed class FaultingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("secret endpoint token"));
    }
}
