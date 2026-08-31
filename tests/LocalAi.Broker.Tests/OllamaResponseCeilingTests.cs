using System.Net;
using System.Text;
using LocalAi.Broker;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The success-body ceiling of #205: the error path always had a bounded reader, success
/// content did not, and a misbehaving endpoint could feed the broker an arbitrarily large
/// body on the single queue everyone shares.
/// </summary>
public sealed class OllamaResponseCeilingTests
{
    private static readonly Uri BaseUri = new("http://ollama.test:11434/");

    [Fact]
    public async Task A_declared_oversized_body_is_refused_before_it_is_read()
    {
        var fake = new FakeOllamaServer();
        fake.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DeclaredLengthContent(200L * 1024 * 1024),
        }));
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => transport.ListInstalledAsync(TestContext.Current.CancellationToken));

        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chunked_body_past_the_ceiling_is_refused_mid_read()
    {
        var fake = new FakeOllamaServer();
        fake.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            // No declared length: the counting stream is the only thing that can stop it.
            Content = new EndlessArrayContent(),
        }));
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => transport.ListInstalledAsync(TestContext.Current.CancellationToken));

        Assert.Contains("exceeded", error.Message, StringComparison.Ordinal);
    }

    private static Task NoDelay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Claims an enormous Content-Length without materializing a byte of it.</summary>
    private sealed class DeclaredLengthContent(long declaredLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    /// <summary>
    /// A JSON document that never ends, pulled rather than pushed: the default
    /// HttpContent buffering would otherwise materialize it before the ceiling could act.
    /// </summary>
    private sealed class EndlessArrayContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context) =>
            throw new NotSupportedException(
                "This content must be read through its stream.");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new EndlessJsonStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class EndlessJsonStream : Stream
        {
            private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("{\"models\":[");
            private long _position;

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
                for (var index = 0; index < count; index++)
                {
                    buffer[offset + index] = _position < Prefix.Length
                        ? Prefix[_position]
                        : (byte)' ';
                    _position++;
                }

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
    }
}
