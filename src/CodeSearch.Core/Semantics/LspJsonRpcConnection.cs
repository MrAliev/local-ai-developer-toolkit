using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodeSearch.Core.Semantics;

public sealed class LspProtocolException(string message) : IOException(message);

/// <summary>Bounded Content-Length JSON-RPC 2.0 connection used by stdio language servers.</summary>
public sealed class LspJsonRpcConnection : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 8 * 1024;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly int _maximumMessageBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private long _nextId;
    private int _disposed;

    public LspJsonRpcConnection(
        Stream input,
        Stream output,
        int maximumMessageBytes = 16 * 1024 * 1024)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        if (maximumMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        }

        _maximumMessageBytes = maximumMessageBytes;
        _reader = ReadLoopAsync();
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Duplicate LSP request identifier.");
        }

        try
        {
            await WriteMessageAsync(
                new { jsonrpc = "2.0", id, method, @params = parameters },
                cancellationToken);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token,
                timeoutSource.Token);
            try
            {
                return await completion.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (
                _lifetime.IsCancellationRequested && completion.Task.IsCompleted)
            {
                return await completion.Task;
            }
            catch (OperationCanceledException) when (
                timeoutSource.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested &&
                !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Language server did not answer '{method}' within {timeout.TotalSeconds:F1}s.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return WriteMessageAsync(
            new { jsonrpc = "2.0", method, @params = parameters },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        try
        {
            await _reader;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }

        CompletePending(new ObjectDisposedException(nameof(LspJsonRpcConnection)));
        _lifetime.Dispose();
        _writeGate.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var payload = await ReadFrameAsync(_lifetime.Token);
                if (payload is null)
                {
                    throw new EndOfStreamException("Language server closed its output stream.");
                }

                using var document = JsonDocument.Parse(payload);
                await DispatchAsync(document.RootElement, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CompletePending(exception);
            _lifetime.Cancel();
        }
    }

    private async Task DispatchAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("jsonrpc", out var version) ||
            version.GetString() != "2.0")
        {
            throw new LspProtocolException("Language server sent an invalid JSON-RPC message.");
        }

        if (message.TryGetProperty("id", out var idElement) &&
            !message.TryGetProperty("method", out _))
        {
            if (!idElement.TryGetInt64(out var id) || !_pending.TryRemove(id, out var completion))
            {
                return;
            }

            if (message.TryGetProperty("error", out var error))
            {
                completion.TrySetException(new LspProtocolException(FormatError(error)));
            }
            else if (message.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(result.Clone());
            }
            else
            {
                completion.TrySetException(new LspProtocolException(
                    "Language server response has neither result nor error."));
            }

            return;
        }

        if (idElement.ValueKind != JsonValueKind.Undefined &&
            message.TryGetProperty("method", out var methodElement))
        {
            object? result = methodElement.GetString() == "workspace/configuration" ? Array.Empty<object>() : null;
            await WriteMessageAsync(
                new { jsonrpc = "2.0", id = JsonSerializer.Deserialize<object>(idElement), result },
                cancellationToken);
        }
    }

    private async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        int? contentLength = null;
        var headerBytes = 0;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            headerBytes += Encoding.ASCII.GetByteCount(line) + 2;
            if (headerBytes > MaximumHeaderBytes)
            {
                throw new LspProtocolException("Language server header is too large.");
            }

            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new LspProtocolException("Language server header is malformed.");
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (contentLength is not null || !int.TryParse(value, out var parsed) ||
                    parsed < 0 || parsed > _maximumMessageBytes)
                {
                    throw new LspProtocolException("Language server Content-Length is invalid.");
                }

                contentLength = parsed;
            }
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                     !value.Contains("utf-8", StringComparison.OrdinalIgnoreCase) &&
                     !value.Contains("utf8", StringComparison.OrdinalIgnoreCase))
            {
                throw new LspProtocolException("Language server used a non-UTF-8 Content-Type.");
            }
        }

        if (contentLength is null)
        {
            throw new LspProtocolException("Language server omitted Content-Length.");
        }

        var payload = new byte[contentLength.Value];
        await _input.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            var count = await _input.ReadAsync(single, cancellationToken);
            if (count == 0)
            {
                return buffer.Length == 0
                    ? null
                    : throw new LspProtocolException("Language server header ended unexpectedly.");
            }

            buffer.WriteByte(single[0]);
            if (buffer.Length > MaximumHeaderBytes)
            {
                throw new LspProtocolException("Language server header line is too large.");
            }

            if (single[0] == (byte)'\n')
            {
                var bytes = buffer.ToArray();
                if (bytes.Length < 2 || bytes[^2] != (byte)'\r')
                {
                    throw new LspProtocolException("Language server headers must use CRLF.");
                }

                return Encoding.ASCII.GetString(bytes, 0, bytes.Length - 2);
            }
        }
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > _maximumMessageBytes)
        {
            throw new LspProtocolException("Outgoing language server message is too large.");
        }

        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(header, cancellationToken);
            await _output.WriteAsync(payload, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void CompletePending(Exception exception)
    {
        foreach (var pending in _pending.ToArray())
        {
            if (_pending.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static string FormatError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement)
            ? codeElement.GetRawText()
            : "unknown";
        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : "unknown error";
        return $"Language server error {code}: {message}";
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
