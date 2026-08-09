using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public class LspJsonRpcConnectionTests
{
    [Fact]
    public async Task FramesARequestAndRoutesItsResponseByIdentifier()
    {
        var streams = new TestDuplex();
        await using var connection = new LspJsonRpcConnection(
            streams.ClientInput,
            streams.ClientOutput,
            maximumMessageBytes: 4096);
        var request = connection.RequestAsync(
            "textDocument/definition",
            new { value = "Ж" },
            TimeSpan.FromSeconds(5),
            Ct);

        using var sent = await ReadFrameAsync(streams.ServerInput, Ct);
        Assert.Equal("2.0", sent.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("textDocument/definition", sent.RootElement.GetProperty("method").GetString());
        Assert.Equal("Ж", sent.RootElement.GetProperty("params").GetProperty("value").GetString());
        var id = sent.RootElement.GetProperty("id").GetInt64();
        await WriteFrameAsync(
            streams.ServerOutput,
            new { jsonrpc = "2.0", id, result = new { answer = 42 } },
            Ct);

        var result = await request;

        Assert.Equal(42, result.GetProperty("answer").GetInt32());
    }

    [Fact]
    public async Task AnswersServerConfigurationRequestsToAvoidDeadlock()
    {
        var streams = new TestDuplex();
        await using var connection = new LspJsonRpcConnection(
            streams.ClientInput,
            streams.ClientOutput,
            maximumMessageBytes: 4096);
        await WriteFrameAsync(
            streams.ServerOutput,
            new
            {
                jsonrpc = "2.0",
                id = 99,
                method = "workspace/configuration",
                @params = new { items = Array.Empty<object>() },
            },
            Ct);

        using var response = await ReadFrameAsync(streams.ServerInput, Ct);

        Assert.Equal(99, response.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Array, response.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task FailsPendingRequestsWhenAFrameExceedsTheConfiguredBound()
    {
        var streams = new TestDuplex();
        await using var connection = new LspJsonRpcConnection(
            streams.ClientInput,
            streams.ClientOutput,
            maximumMessageBytes: 128);
        var request = connection.RequestAsync(
            "bounded",
            null,
            TimeSpan.FromSeconds(5),
            Ct);
        using var sent = await ReadFrameAsync(streams.ServerInput, Ct);
        var header = Encoding.ASCII.GetBytes("Content-Length: 129\r\n\r\n");
        await streams.ServerOutput.WriteAsync(header, Ct);

        await Assert.ThrowsAsync<LspProtocolException>(() => request);
    }

    [Fact]
    public async Task TimesOutAnUnansweredRequest()
    {
        var streams = new TestDuplex();
        await using var connection = new LspJsonRpcConnection(
            streams.ClientInput,
            streams.ClientOutput,
            maximumMessageBytes: 4096);

        await Assert.ThrowsAsync<TimeoutException>(() => connection.RequestAsync(
            "never",
            null,
            TimeSpan.FromMilliseconds(20),
            Ct));
    }

    [Fact]
    public void ParsesLocationsAndDefinitionLinks()
    {
        using var document = JsonDocument.Parse("""
            [
              {"uri":"file:///a.cs","range":{"start":{"line":1,"character":2},"end":{"line":1,"character":4}}},
              {"targetUri":"file:///b.cs","targetSelectionRange":{"start":{"line":3,"character":5},"end":{"line":3,"character":8}}}
            ]
            """);

        var locations = StdioLanguageServerClient.ParseLocations(
            document.RootElement,
            allowLocationLinks: true);

        Assert.Equal(2, locations.Count);
        Assert.Equal(new SourceRange(3, 5, 3, 8), locations[1].Range);
    }

    [Fact]
    public void NormalizesNodeStyleWindowsDriveUris()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows drive URI compatibility test.");
        }

        using var document = JsonDocument.Parse("""
            {"uri":"file:///c%3A/Source/App.cs","range":{"start":{"line":1,"character":2},"end":{"line":1,"character":4}}}
            """);

        var location = Assert.Single(
            StdioLanguageServerClient.ParseLocations(document.RootElement, false));

        Assert.Equal(
            Path.GetFullPath("C:\\Source\\App.cs"),
            Path.GetFullPath(location.Uri.LocalPath),
            ignoreCase: true);
    }

    [Fact]
    public void RejectsInvalidLocationRanges()
    {
        using var document = JsonDocument.Parse("""
            {"uri":"file:///a.cs","range":{"start":{"line":2,"character":4},"end":{"line":1,"character":4}}}
            """);

        Assert.Throws<LspProtocolException>(() =>
            StdioLanguageServerClient.ParseLocations(document.RootElement, false));
    }

    [Fact]
    public async Task ProtocolClientInitializesSynchronizesAndQueriesDefinitions()
    {
        var streams = new TestDuplex();
        await using var connection = new LspJsonRpcConnection(
            streams.ClientInput,
            streams.ClientOutput,
            maximumMessageBytes: 16 * 1024);
        await using var client = StdioLanguageServerClient.CreateForTesting(
            connection,
            new LanguageServerProcessSpec(
                "fixture",
                [],
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaximumMessageBytes: 16 * 1024));
        var root = Path.Combine(Path.GetTempPath(), "lsp-protocol-client");
        var initialize = client.InitializeAsync(root, Ct);
        using var initializeRequest = await ReadFrameAsync(streams.ServerInput, Ct);
        Assert.Equal("initialize", initializeRequest.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "utf-16",
            initializeRequest.RootElement.GetProperty("params").GetProperty("capabilities")
                .GetProperty("general").GetProperty("positionEncodings")[0].GetString());
        await WriteFrameAsync(
            streams.ServerOutput,
            new
            {
                jsonrpc = "2.0",
                id = initializeRequest.RootElement.GetProperty("id").GetInt64(),
                result = new
                {
                    capabilities = new
                    {
                        definitionProvider = true,
                        implementationProvider = true,
                        textDocumentSync = 1,
                    },
                },
            },
            Ct);
        await initialize;
        using var initialized = await ReadFrameAsync(streams.ServerInput, Ct);
        Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());

        var document = new LspTextDocument(
            new Uri(Path.Combine(root, "Use.ts"), UriKind.Absolute),
            "typescript",
            1,
            "run();");
        await client.DidOpenAsync(document, Ct);
        using var opened = await ReadFrameAsync(streams.ServerInput, Ct);
        Assert.Equal("textDocument/didOpen", opened.RootElement.GetProperty("method").GetString());
        Assert.Equal("run();", opened.RootElement.GetProperty("params")
            .GetProperty("textDocument").GetProperty("text").GetString());

        var definition = client.GoToDefinitionAsync(document.Uri, 0, 1, Ct);
        using var definitionRequest = await ReadFrameAsync(streams.ServerInput, Ct);
        await WriteFrameAsync(
            streams.ServerOutput,
            new
            {
                jsonrpc = "2.0",
                id = definitionRequest.RootElement.GetProperty("id").GetInt64(),
                result = new[]
                {
                    new
                    {
                        targetUri = new Uri(Path.Combine(root, "Def.ts"), UriKind.Absolute).AbsoluteUri,
                        targetSelectionRange = new
                        {
                            start = new { line = 2, character = 3 },
                            end = new { line = 2, character = 6 },
                        },
                    },
                },
            },
            Ct);

        var location = Assert.Single(await definition);
        Assert.Equal(new SourceRange(2, 3, 2, 6), location.Range);

        var implementation = client.FindImplementationsAsync(document.Uri, 0, 1, Ct);
        using var implementationRequest = await ReadFrameAsync(streams.ServerInput, Ct);
        Assert.Equal(
            "textDocument/implementation",
            implementationRequest.RootElement.GetProperty("method").GetString());
        await WriteFrameAsync(
            streams.ServerOutput,
            new
            {
                jsonrpc = "2.0",
                id = implementationRequest.RootElement.GetProperty("id").GetInt64(),
                result = new[]
                {
                    new
                    {
                        uri = new Uri(Path.Combine(root, "Impl.ts"), UriKind.Absolute).AbsoluteUri,
                        range = new
                        {
                            start = new { line = 4, character = 1 },
                            end = new { line = 4, character = 4 },
                        },
                    },
                },
            },
            Ct);
        Assert.Equal(
            new SourceRange(4, 1, 4, 4),
            Assert.Single(await implementation).Range);
    }

    /// <summary>
    /// The member has to arrive on the wire, spelled the way the specification spells it and
    /// carrying exactly what was configured. Asserting on the spec object instead would pass
    /// while the client quietly dropped it, which is the state this fixes: typescript-language-
    /// server needs <c>tsserver.path</c> to run at all in a workspace without its own TypeScript,
    /// and there was no way to send it.
    /// </summary>
    [Fact]
    public async Task ConfiguredInitializationOptionsReachTheServerVerbatim()
    {
        using var request = await CaptureInitializeRequestAsync(
            JsonSerializer.SerializeToElement(
                new { tsserver = new { path = "/opt/typescript/lib/tsserver.js" } }));

        Assert.Equal(
            "/opt/typescript/lib/tsserver.js",
            request.RootElement.GetProperty("params")
                .GetProperty("initializationOptions")
                .GetProperty("tsserver")
                .GetProperty("path")
                .GetString());
    }

    /// <summary>
    /// Absent, not null. A server that reads the member without checking gets a null where it
    /// expects an object, so "nothing configured" has to mean the key is not there at all.
    /// </summary>
    [Fact]
    public async Task NoConfiguredOptionsOmitTheMemberRatherThanSendingNull()
    {
        using var request = await CaptureInitializeRequestAsync(null);

        Assert.False(
            request.RootElement.GetProperty("params")
                .TryGetProperty("initializationOptions", out _));
    }

    /// <summary>Runs the initialize handshake and returns the request the server saw.</summary>
    private async Task<JsonDocument> CaptureInitializeRequestAsync(
        JsonElement? initializationOptions)
    {
        var streams = new TestDuplex();
        await using var connection = new LspJsonRpcConnection(
            streams.ClientInput,
            streams.ClientOutput,
            maximumMessageBytes: 16 * 1024);
        await using var client = StdioLanguageServerClient.CreateForTesting(
            connection,
            new LanguageServerProcessSpec(
                "fixture",
                [],
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaximumMessageBytes: 16 * 1024,
                InitializationOptions: initializationOptions));
        var initialize = client.InitializeAsync(
            Path.Combine(Path.GetTempPath(), "lsp-initialization-options"),
            Ct);
        var request = await ReadFrameAsync(streams.ServerInput, Ct);
        await WriteFrameAsync(
            streams.ServerOutput,
            new
            {
                jsonrpc = "2.0",
                id = request.RootElement.GetProperty("id").GetInt64(),
                result = new { capabilities = new { textDocumentSync = 1 } },
            },
            Ct);
        await initialize;
        using var initialized = await ReadFrameAsync(streams.ServerInput, Ct);
        return request;
    }

    [Fact]
    public async Task IncrementalServersReceiveAFullDocumentReplacementRangeInUtf16()
    {
        var streams = new TestDuplex();
        await using var connection = new LspJsonRpcConnection(
            streams.ClientInput,
            streams.ClientOutput,
            maximumMessageBytes: 16 * 1024);
        await using var client = StdioLanguageServerClient.CreateForTesting(
            connection,
            new LanguageServerProcessSpec(
                "fixture", [], RequestTimeout: TimeSpan.FromSeconds(5),
                MaximumMessageBytes: 16 * 1024));
        var root = Path.Combine(Path.GetTempPath(), "lsp-incremental-client");
        var initialize = client.InitializeAsync(root, Ct);
        using var initializeRequest = await ReadFrameAsync(streams.ServerInput, Ct);
        await WriteFrameAsync(
            streams.ServerOutput,
            new
            {
                jsonrpc = "2.0",
                id = initializeRequest.RootElement.GetProperty("id").GetInt64(),
                result = new { capabilities = new { textDocumentSync = 2 } },
            },
            Ct);
        await initialize;
        using var initialized = await ReadFrameAsync(streams.ServerInput, Ct);
        var uri = new Uri(Path.Combine(root, "Use.ts"), UriKind.Absolute);
        await client.DidOpenAsync(
            new LspTextDocument(uri, "typescript", 1, "A😀\r\nBC"), Ct);
        using var opened = await ReadFrameAsync(streams.ServerInput, Ct);

        await client.DidChangeAsync(
            new LspTextDocument(uri, "typescript", 2, "changed"), Ct);
        using var changed = await ReadFrameAsync(streams.ServerInput, Ct);

        var change = changed.RootElement.GetProperty("params").GetProperty("contentChanges")[0];
        Assert.Equal(7, change.GetProperty("rangeLength").GetInt32());
        Assert.Equal(1, change.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(2, change.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task UnsupportedNavigationCapabilityReturnsEmptyWithoutAProtocolError()
    {
        var streams = new TestDuplex();
        await using var connection = new LspJsonRpcConnection(
            streams.ClientInput,
            streams.ClientOutput,
            maximumMessageBytes: 16 * 1024);
        await using var client = StdioLanguageServerClient.CreateForTesting(
            connection,
            new LanguageServerProcessSpec(
                "fixture", [], RequestTimeout: TimeSpan.FromSeconds(5),
                MaximumMessageBytes: 16 * 1024));
        var root = Path.Combine(Path.GetTempPath(), "lsp-capability-client");
        var initialize = client.InitializeAsync(root, Ct);
        using var initializeRequest = await ReadFrameAsync(streams.ServerInput, Ct);
        await WriteFrameAsync(
            streams.ServerOutput,
            new
            {
                jsonrpc = "2.0",
                id = initializeRequest.RootElement.GetProperty("id").GetInt64(),
                result = new { capabilities = new { textDocumentSync = 1 } },
            },
            Ct);
        await initialize;
        using var initialized = await ReadFrameAsync(streams.ServerInput, Ct);

        var definitions = await client.GoToDefinitionAsync(
            new Uri(Path.Combine(root, "index.html"), UriKind.Absolute), 0, 0, Ct);
        var references = await client.FindReferencesAsync(
            new Uri(Path.Combine(root, "index.html"), UriKind.Absolute), 0, 0, true, Ct);
        var implementations = await client.FindImplementationsAsync(
            new Uri(Path.Combine(root, "index.html"), UriKind.Absolute), 0, 0, Ct);

        Assert.Empty(definitions);
        Assert.Empty(references);
        Assert.Empty(implementations);
    }

    private static async Task<JsonDocument> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        var tail = 0;
        var single = new byte[1];
        while (tail != 0x0D0A0D0A)
        {
            await stream.ReadExactlyAsync(single, cancellationToken);
            header.Add(single[0]);
            tail = (tail << 8 | single[0]) & unchecked((int)0xffffffff);
        }

        var headerText = Encoding.ASCII.GetString(header.ToArray());
        var lengthLine = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var length = int.Parse(lengthLine[(lengthLine.IndexOf(':') + 1)..].Trim());
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonDocument.Parse(payload);
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        object value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class TestDuplex
    {
        private readonly Channel<byte[]> _clientToServer = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _serverToClient = Channel.CreateUnbounded<byte[]>();

        public Stream ClientInput => new ChannelReadStream(_serverToClient.Reader);
        public Stream ClientOutput => new ChannelWriteStream(_clientToServer.Writer);
        public Stream ServerInput => new ChannelReadStream(_clientToServer.Reader);
        public Stream ServerOutput => new ChannelWriteStream(_serverToClient.Writer);
    }

    private sealed class ChannelReadStream(ChannelReader<byte[]> reader) : Stream
    {
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                _current = await reader.ReadAsync(cancellationToken);
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChannelWriteStream(ChannelWriter<byte[]> writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            writer.WriteAsync(buffer.ToArray(), cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            writer.WriteAsync(buffer.AsMemory(offset, count).ToArray()).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
