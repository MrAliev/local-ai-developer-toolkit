using System.Net;
using System.Net.Http.Headers;

namespace LocalAi.Installer.Core.Releases;

public sealed class HttpReleaseClient : Abstractions.IReleaseClient, IDisposable
{
    private readonly HttpClient client;

    public HttpReleaseClient()
        : this(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        })
    {
    }

    public HttpReleaseClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        client = new HttpClient(handler, disposeHandler: true);
    }

    public async Task<Stream> OpenPackageAsync(
        Uri approvedPackageUri,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedPackageUri);
        if (!approvedPackageUri.IsAbsoluteUri ||
            !string.Equals(
                approvedPackageUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal) ||
            maximumBytes <= 0)
        {
            throw Failure();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, approvedPackageUri);
        request.Headers.AcceptEncoding.Clear();
        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Headers.Location is not null ||
                response.Headers.TransferEncoding.Count != 0 ||
                response.Content.Headers.ContentEncoding.Count != 0 ||
                response.Content.Headers.ContentLength is not { } contentLength ||
                contentLength != maximumBytes)
            {
                response.Dispose();
                throw Failure();
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var owned = new ResponseOwnedStream(stream, response);
            response = null;
            return owned;
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            throw;
        }
        catch (ReleaseVerificationException)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or
            InvalidOperationException or NotSupportedException)
        {
            response?.Dispose();
            throw Failure();
        }
    }

    public void Dispose() => client.Dispose();

    private static ReleaseVerificationException Failure() =>
        new("Release download failed.");

    private sealed class ResponseOwnedStream(
        Stream inner,
        HttpResponseMessage response) : Stream
    {
        private bool disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException)
            {
                throw Failure();
            }
        }

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException)
            {
                throw Failure();
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException)
            {
                throw Failure();
            }
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            try
            {
                return await inner.ReadAsync(buffer, offset, count, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException)
            {
                throw Failure();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
                response.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }
}
