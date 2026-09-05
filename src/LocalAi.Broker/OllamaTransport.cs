using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class OllamaTransport : IModelRuntimeTransport, IDisposable
{
    private const int MaxAttempts = 3;
    private const int MaxErrorBodyCharacters = 400;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions ExternalResponseJson =
        CreateExternalResponseJsonOptions();

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;
    private readonly bool _ownsHttpClient;
    private readonly object _activeModelGate = new();
    private string? _activeModel;
    private int _activeModelObserved;

    /// <summary>
    /// Which models must have their reasoning switched off for routed chat. A reasoning model
    /// that thinks its whole window away returns a full `thinking` and an empty `content`,
    /// which fails as "empty chat content" and lands on the fallback — on the reference
    /// machine, sixteen jobs in a month, every one of them qwen3.5:9b.
    /// </summary>
    private readonly Func<string, bool>? _disableThinking;

    public string? ActiveModel => Volatile.Read(ref _activeModel);

    public OllamaTransport(
        HttpClient httpClient,
        Uri baseUri,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        Func<string, bool>? disableThinking = null)
        : this(httpClient, baseUri, retryDelay, disableThinking, ownsHttpClient: false)
    {
    }

    public OllamaTransport(
        Uri baseUri,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        Func<string, bool>? disableThinking = null)
        : this(CreateOwnedHttpClient(), baseUri, retryDelay, disableThinking, ownsHttpClient: true)
    {
    }

    private OllamaTransport(
        HttpClient httpClient,
        Uri baseUri,
        Func<TimeSpan, CancellationToken, Task>? retryDelay,
        Func<string, bool>? disableThinking,
        bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        if (!_baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Base URI must be absolute.", nameof(baseUri));
        }

        _retryDelay = retryDelay ?? Task.Delay;
        _disableThinking = disableThinking;
        _ownsHttpClient = ownsHttpClient;
    }

    public Task<BrokerExecutionResult> ExecuteAsync(
        LocalJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Payload switch
        {
            EmbedJobPayload payload => ExecuteEmbedAsync(payload, cancellationToken),
            ChatJobPayload payload => ExecuteChatAsync(payload, cancellationToken),
            ListModelsJobPayload payload => ExecuteListModelsAsync(cancellationToken),
            NativeOllamaJobPayload payload => ExecuteNativeAsync(payload, cancellationToken),
            _ => throw new NotSupportedException(
                $"Unsupported payload type '{request.Payload.GetType().Name}'.")
        };
    }

    public async Task<IReadOnlyList<string>> ListInstalledAsync(
        CancellationToken cancellationToken)
    {
        using var document = await SendAsync(
            HttpMethod.Get,
            "api/tags",
            body: null,
            sensitiveValues: [],
            cancellationToken);
        TagsResponse response;
        try
        {
            response = document.RootElement.Deserialize<TagsResponse>(ExternalResponseJson)
                ?? throw new InvalidDataException("Ollama returned a null tags response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Ollama returned an invalid tags response.", exception);
        }

        if (response.Models is null ||
            response.Models.Any(model =>
                model is null || string.IsNullOrWhiteSpace(model.Name)))
        {
            throw new InvalidDataException("Ollama returned a blank model name.");
        }

        return Array.AsReadOnly(
            response.Models
                .Select(model => model.Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    public async Task<IReadOnlyList<OllamaProcessInfo>> ListProcessesAsync(
        CancellationToken cancellationToken)
    {
        using var document = await SendAsync(
            HttpMethod.Get,
            "api/ps",
            body: null,
            sensitiveValues: [],
            cancellationToken);
        ProcessResponse response;
        try
        {
            response = document.RootElement.Deserialize<ProcessResponse>(ExternalResponseJson)
                ?? throw new InvalidDataException("Ollama returned a null process response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Ollama returned an invalid process response.",
                exception);
        }

        if (response.Models is null)
        {
            throw new InvalidDataException("Ollama returned a null process list.");
        }

        var processes = new List<OllamaProcessInfo>(response.Models.Count);
        foreach (var model in response.Models)
        {
            if (model is null ||
                string.IsNullOrWhiteSpace(model.Name) ||
                model.Size <= 0 ||
                model.SizeVram <= 0 ||
                model.ContextLength <= 0 ||
                model.ExpiresAt == default)
            {
                throw new InvalidDataException(
                    "Ollama returned an invalid process entry.");
            }

            processes.Add(new OllamaProcessInfo(
                model.Name,
                model.Size,
                model.SizeVram,
                model.ContextLength,
                model.ExpiresAt.ToUniversalTime()));
        }

        var active = ActiveModel;
        if (active is not null && processes.Any(process => string.Equals(
                process.Model,
                active,
                StringComparison.Ordinal)))
        {
            Volatile.Write(ref _activeModelObserved, 1);
        }

        return processes.AsReadOnly();
    }

    public async Task<BackendProbeResult> ProbeActiveModelAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var processes = await ListProcessesAsync(cancellationToken);
            var expected = ActiveModel;
            if (expected is not null &&
                !processes.Any(process => string.Equals(
                    process.Model,
                    expected,
                    StringComparison.Ordinal)))
            {
                return Volatile.Read(ref _activeModelObserved) == 1
                    ? BackendProbeResult.Unhealthy("active_model_missing")
                    : BackendProbeResult.Inconclusive("active_model_loading");
            }

            return BackendProbeResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BackendProbeResult.Inconclusive("probe_cancelled");
        }
        catch (HttpRequestException exception)
        {
            return BackendProbeResult.Unhealthy(
                exception.StatusCode is null
                    ? "backend_unreachable"
                    : $"backend_http_{(int)exception.StatusCode.Value}");
        }
        catch (IOException)
        {
            return BackendProbeResult.Unhealthy("backend_io_failure");
        }
        catch
        {
            return BackendProbeResult.Inconclusive("probe_failed");
        }
    }

    /// <summary>
    /// Streamed, because a pull is gigabytes and minutes and the only place its size is known is
    /// inside it. Unstreamed, this call sat silent until the download ended and then returned one
    /// object; nothing above it could say anything true about how far it had got.
    ///
    /// Each line is one JSON object. A line that cannot be read is skipped rather than fatal: the
    /// stream is an encouragement and the answer is the last line, which is checked.
    /// </summary>
    public async Task PullAsync(
        string model,
        Func<ModelPullProgress, CancellationToken, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var body = JsonSerializer.Serialize(new PullRequest(model, Stream: true));
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await TryPullAsync(model, body, onProgress, cancellationToken))
                {
                    return;
                }

                throw new InvalidDataException("Ollama did not confirm model pull success.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode is null && attempt < MaxAttempts)
            {
                // A retry restarts the counters, and the reader sees them restart. That is
                // honest: the layers already on disk are skipped, so the second pass is short.
                await _retryDelay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                await _retryDelay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }

        throw new InvalidDataException("Ollama did not confirm model pull success.");
    }

    private async Task<bool> TryPullAsync(
        string model,
        string body,
        Func<ModelPullProgress, CancellationToken, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/pull"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedErrorBodyAsync(response.Content, cancellationToken);
            throw CreateStatusException(
                response,
                Redact(errorBody.Text, errorBody.IsTruncated, [model]));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var bounded = new BoundedReadStream(
            stream,
            DefaultResponseCeilingBytes,
            "api/pull");
        using var reader = new StreamReader(bounded, Encoding.UTF8);
        var succeeded = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            PullStreamLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<PullStreamLine>(line, ExternalResponseJson);
            }
            catch (JsonException)
            {
                // One unreadable line is not a failed download. The answer is the last line.
                continue;
            }

            if (parsed?.Status is not { } status)
            {
                continue;
            }

            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                succeeded = true;
            }

            if (onProgress is not null)
            {
                await onProgress(
                    new ModelPullProgress(
                        status,
                        parsed.Digest,
                        parsed.Completed ?? 0,
                        parsed.Total ?? 0),
                    cancellationToken);
            }
        }

        return succeeded;
    }

    public async Task PreflightAsync(
        string model,
        int contextTokens,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        TrackActiveModel(model);
        if (!LocalContextTiers.IsSupported(contextTokens))
        {
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        }

        var body = JsonSerializer.Serialize(new GenerateRequest(
            model,
            string.Empty,
            Stream: false,
            KeepAlive: "30m",
            new GenerateOptions(contextTokens)));
        using var document = await SendAsync(
            HttpMethod.Post,
            "api/generate",
            body,
            [model],
            cancellationToken);
        RequireObject(document, "preflight");
    }

    public async Task PreflightEmbeddingAsync(
        string model,
        int contextTokens,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        TrackActiveModel(model);
        if (!LocalContextTiers.IsSupported(contextTokens))
        {
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        }

        const string input = "localai-preflight";
        var body = JsonSerializer.Serialize(new EmbedRequest(
            model,
            [input],
            KeepAlive: "30m",
            new GenerateOptions(contextTokens)));
        using var document = await SendAsync(
            HttpMethod.Post,
            "api/embed",
            body,
            [input],
            cancellationToken,
            maximumResponseBytes: EmbedResponseCeilingBytes);
        var response = document.RootElement.Deserialize<EmbedResponse>(
            ExternalResponseJson);
        if (response?.Embeddings is not [var embedding] ||
            embedding.Count == 0)
        {
            throw new InvalidDataException(
                "Ollama returned an invalid embedding preflight response.");
        }
    }

    public async Task UnloadAsync(
        string model,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var body = JsonSerializer.Serialize(new GenerateRequest(
            model,
            string.Empty,
            Stream: false,
            KeepAlive: 0,
            Options: null));
        using var document = await SendAsync(
            HttpMethod.Post,
            "api/generate",
            body,
            [model],
            cancellationToken);
        RequireObject(document, "unload");
        lock (_activeModelGate)
        {
            if (string.Equals(_activeModel, model, StringComparison.Ordinal))
            {
                Volatile.Write(ref _activeModel, null);
                Volatile.Write(ref _activeModelObserved, 0);
            }
        }
    }

    private async Task<BrokerExecutionResult> ExecuteNativeAsync(
        NativeOllamaJobPayload payload,
        CancellationToken cancellationToken)
    {
        var (method, path) = payload.Operation switch
        {
            NativeOllamaOperation.Chat => (HttpMethod.Post, "api/chat"),
            NativeOllamaOperation.Embed => (HttpMethod.Post, "api/embed"),
            NativeOllamaOperation.Tags => (HttpMethod.Get, "api/tags"),
            NativeOllamaOperation.Show => (HttpMethod.Post, "api/show"),
            NativeOllamaOperation.Processes => (HttpMethod.Get, "api/ps"),
            NativeOllamaOperation.Generate => (HttpMethod.Post, "api/generate"),
            _ => throw new ArgumentOutOfRangeException(nameof(payload))
        };
        if (method == HttpMethod.Post &&
            payload.RequestBody is not { ValueKind: JsonValueKind.Object })
        {
            throw new InvalidOperationException(
                $"Native operation '{payload.Operation}' requires an object request body.");
        }

        var body = method == HttpMethod.Post
            ? payload.RequestBody!.Value.GetRawText()
            : null;
        using var document = await SendAsync(
            method,
            path,
            body,
            [],
            cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Ollama returned a non-object native response.");
        }

        return Result(new NativeOllamaJobOutput(document.RootElement.Clone()));
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Counts what the JSON parser actually consumes and refuses past the ceiling, because a
    /// chunked response carries no Content-Length to refuse early (#205).
    /// </summary>
    private sealed class BoundedReadStream(
        Stream inner,
        long limit,
        string relativePath) : Stream
    {
        private long _consumed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private int Count(int read)
        {
            _consumed += read;
            if (_consumed > limit)
            {
                throw new InvalidDataException(
                    $"Ollama's response for '{relativePath}' exceeded the {limit}-byte limit.");
            }

            return read;
        }
    }

    private async Task<BrokerExecutionResult> ExecuteEmbedAsync(
        EmbedJobPayload payload,
        CancellationToken cancellationToken)
    {
        TrackActiveModel(payload.Model);
        var body = JsonSerializer.Serialize(new EmbedRequest(
            payload.Model,
            payload.Inputs,
            Options: payload.RequestedContextTokens is { } contextTokens
                ? new GenerateOptions(contextTokens)
                : null));
        using var document = await SendAsync(
            HttpMethod.Post,
            "api/embed",
            body,
            payload.Inputs,
            cancellationToken,
            maximumResponseBytes: EmbedResponseCeilingBytes);
        EmbedResponse response;
        try
        {
            response = document.RootElement.Deserialize<EmbedResponse>(ExternalResponseJson)
                ?? throw new InvalidDataException("Ollama returned a null embed response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Ollama returned an invalid embed response.", exception);
        }

        if (response.Embeddings is null ||
            response.Embeddings.Count != payload.Inputs.Count)
        {
            throw new InvalidDataException(
                "Ollama returned an unexpected number of embedding vectors.");
        }

        int? dimensions = null;
        foreach (var vector in response.Embeddings)
        {
            if (vector is null || vector.Count == 0)
            {
                throw new InvalidDataException("Ollama returned an empty embedding vector.");
            }

            if (vector.Any(value => !double.IsFinite(value)))
            {
                throw new InvalidDataException(
                    "Ollama returned a non-finite embedding value.");
            }

            dimensions ??= vector.Count;
            if (dimensions != vector.Count)
            {
                throw new InvalidDataException(
                    "Ollama returned embedding vectors with inconsistent dimensions.");
            }
        }

        return Result(new EmbedJobOutput(response.Embeddings));
    }

    private async Task<BrokerExecutionResult> ExecuteChatAsync(
        ChatJobPayload payload,
        CancellationToken cancellationToken)
    {
        var model = payload.Model
            ?? throw new InvalidOperationException(
                "Routed chat must be resolved to a concrete model before transport.");
        TrackActiveModel(model);
        var messages = new List<ChatRequestMessage>();
        if (payload.System is not null)
        {
            messages.Add(new ChatRequestMessage("system", payload.System, null));
        }

        messages.Add(new ChatRequestMessage(
            "user",
            payload.Prompt,
            payload.ImagesBase64.Count == 0 ? null : payload.ImagesBase64));
        var body = JsonSerializer.Serialize(new ChatRequest(
            model,
            messages,
            Stream: false,
            Think: _disableThinking?.Invoke(model) is true ? false : null,
            payload.RequestedContextTokens is { } contextTokens
                ? new GenerateOptions(contextTokens)
                : null));
        using var document = await SendAsync(
            HttpMethod.Post,
            "api/chat",
            body,
            [payload.Prompt, payload.System ?? string.Empty, .. payload.ImagesBase64],
            cancellationToken);
        ChatResponse response;
        try
        {
            response = document.RootElement.Deserialize<ChatResponse>(ExternalResponseJson)
                ?? throw new InvalidDataException("Ollama returned a null chat response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Ollama returned an invalid chat response.", exception);
        }

        if (response.Message is null ||
            string.IsNullOrWhiteSpace(response.Message.Content))
        {
            throw new InvalidDataException("Ollama returned empty chat content.");
        }

        return Result(new ChatJobOutput(response.Message.Content.Trim()));
    }

    private async Task<BrokerExecutionResult> ExecuteListModelsAsync(
        CancellationToken cancellationToken)
    {
        var names = await ListInstalledAsync(cancellationToken);
        return Result(new ListModelsJobOutput(names));
    }

    private static void RequireObject(JsonDocument document, string operation)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Ollama returned a non-object {operation} response.");
        }
    }

    /// <summary>
    /// The success-body ceiling (#205). The error path always had a bounded reader; success
    /// content went straight into the JSON parser with no size check and an infinite client
    /// timeout, so a misconfigured or misbehaving endpoint could feed the broker an
    /// arbitrarily large body and take the whole single queue down with it. 64MB covers any
    /// chat or status response with orders of magnitude to spare; the embedding call sites
    /// pass their own larger ceiling, because a legitimate batch of 4096-dimension vectors
    /// is the one big body this transport produces. Stall detection stays with the job
    /// watchdog, which already probes and fails an attempt on confirmed unresponsiveness.
    /// </summary>
    private const long DefaultResponseCeilingBytes = 64L * 1024 * 1024;

    internal const long EmbedResponseCeilingBytes = 512L * 1024 * 1024;

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string relativePath,
        string? body,
        IReadOnlyList<string> sensitiveValues,
        CancellationToken cancellationToken,
        long maximumResponseBytes = DefaultResponseCeilingBytes)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(
                    method,
                    new Uri(_baseUri, relativePath));
                if (body is not null)
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    if (response.Content.Headers.ContentLength is { } declared &&
                        declared > maximumResponseBytes)
                    {
                        throw new InvalidDataException(
                            $"Ollama declared a {declared}-byte response for " +
                            $"'{relativePath}', past the {maximumResponseBytes}-byte limit.");
                    }

                    try
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(
                            cancellationToken);
                        await using var bounded = new BoundedReadStream(
                            stream,
                            maximumResponseBytes,
                            relativePath);
                        return await JsonDocument.ParseAsync(
                            bounded,
                            cancellationToken: cancellationToken);
                    }
                    catch (JsonException exception)
                    {
                        throw new InvalidDataException(
                            "Ollama returned malformed JSON.",
                            exception);
                    }
                }

                var errorBody = await ReadBoundedErrorBodyAsync(
                    response.Content,
                    cancellationToken);
                if (!IsRetryable(response.StatusCode, errorBody.Text) ||
                    attempt == MaxAttempts)
                {
                    throw CreateStatusException(
                        response,
                        Redact(
                            errorBody.Text,
                            errorBody.IsTruncated,
                            sensitiveValues));
                }

                var retryAfter = GetBoundedRetryAfter(response.Headers.RetryAfter);
                await _retryDelay(
                    retryAfter ?? TimeSpan.FromSeconds(2 * attempt),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode is null && attempt < MaxAttempts)
            {
                await _retryDelay(
                    TimeSpan.FromSeconds(2 * attempt),
                    cancellationToken);
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                await _retryDelay(
                    TimeSpan.FromSeconds(2 * attempt),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (attempt < MaxAttempts)
            {
                await _retryDelay(
                    TimeSpan.FromSeconds(2 * attempt),
                    cancellationToken);
            }
        }

        throw new InvalidOperationException("Ollama retry loop completed unexpectedly.");
    }

    private static BrokerExecutionResult Result<T>(T output) =>
        new(JsonSerializer.SerializeToElement(output, LocalAiJson.Strict));

    private void TrackActiveModel(string model)
    {
        lock (_activeModelGate)
        {
            if (string.Equals(_activeModel, model, StringComparison.Ordinal))
            {
                return;
            }

            Volatile.Write(ref _activeModel, model);
            Volatile.Write(ref _activeModelObserved, 0);
        }
    }

    private static JsonSerializerOptions CreateExternalResponseJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        };
        options.MakeReadOnly();
        return options;
    }

    private static HttpClient CreateOwnedHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    /// <summary>
    /// A 400 whose body says the server could not reach its own runner is not a bad
    /// request. It is the backend being briefly unavailable, and the same request works
    /// moments later.
    ///
    /// Counted rather than guessed (#349): 40 embed jobs failed this way in four days,
    /// every one an HTTP 400 returned after the same 10-12 seconds a success takes, with
    /// no accompanying line in Ollama's own log, and every one succeeded on a retry. The
    /// layer above answered by halving the batch to isolate an offending chunk, and there
    /// was none - so it failed twice where it could have waited once.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode statusCode, string errorBody) =>
        IsTransient(statusCode) ||
        (statusCode == HttpStatusCode.BadRequest && NamesAnUnreachableRunner(errorBody));

    /// <summary>
    /// Read from the backend's own words, which is why it lives here: this is the one
    /// layer that owns what Ollama says, and the layers above it must not have to learn
    /// the vocabulary. Ollama does not translate these, so matching them is stable in a
    /// way that matching our own text would not be.
    /// </summary>
    private static bool NamesAnUnreachableRunner(string errorBody) =>
        errorBody.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) ||
        errorBody.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
        errorBody.Contains("actively refused", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? GetBoundedRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        var delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } retryAt)
        {
            delay = retryAt - DateTimeOffset.UtcNow;
        }

        if (delay is null ||
            delay < TimeSpan.Zero ||
            delay > MaxRetryAfter)
        {
            return null;
        }

        return delay;
    }

    private static async Task<BoundedErrorBody> ReadBoundedErrorBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 128,
            leaveOpen: true);
        var buffer = new char[MaxErrorBodyCharacters + 1];
        var charactersRead = await reader.ReadBlockAsync(
            buffer.AsMemory(),
            cancellationToken);
        var outputLength = Math.Min(charactersRead, MaxErrorBodyCharacters);
        return new BoundedErrorBody(
            new string(buffer, 0, outputLength),
            charactersRead > MaxErrorBodyCharacters);
    }

    private static HttpRequestException CreateStatusException(
        HttpResponseMessage response,
        string errorBody)
    {
        var excerpt = string.IsNullOrWhiteSpace(errorBody)
            ? string.Empty
            : $" Response: {errorBody}";
        return new HttpRequestException(
            $"Ollama request failed with HTTP {(int)response.StatusCode}.{excerpt}",
            inner: null,
            response.StatusCode);
    }

    private static string Redact(
        string errorBody,
        bool isTruncated,
        IReadOnlyList<string> sensitiveValues)
    {
        var orderedSensitiveValues = sensitiveValues
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (isTruncated && orderedSensitiveValues.Length > 0)
        {
            return "[REDACTED]";
        }

        var redacted = errorBody;
        foreach (var value in orderedSensitiveValues)
        {
            redacted = redacted.Replace(
                value,
                "[REDACTED]",
                StringComparison.Ordinal);
        }

        return redacted.Length <= MaxErrorBodyCharacters
            ? redacted
            : redacted[..MaxErrorBodyCharacters];
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("keep_alive")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? KeepAlive = null,
        [property: JsonPropertyName("options")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        GenerateOptions? Options = null);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")]
        [property: JsonRequired]
        IReadOnlyList<IReadOnlyList<double>>? Embeddings);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatRequestMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("think")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? Think,
        [property: JsonPropertyName("options")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        GenerateOptions? Options);

    private sealed record ChatRequestMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("images")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Images);

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")]
        [property: JsonRequired]
        ChatResponseMessage? Message);

    private sealed record ChatResponseMessage(
        [property: JsonPropertyName("content")]
        [property: JsonRequired]
        string Content);

    private sealed record TagsResponse(
        [property: JsonPropertyName("models")]
        [property: JsonRequired]
        IReadOnlyList<TagModel>? Models);

    private sealed record TagModel(
        [property: JsonPropertyName("name")]
        [property: JsonRequired]
        string Name);

    private sealed record ProcessResponse(
        [property: JsonPropertyName("models")]
        [property: JsonRequired]
        IReadOnlyList<ProcessModel>? Models);

    private sealed record ProcessModel(
        [property: JsonPropertyName("name")]
        [property: JsonRequired]
        string Name,
        [property: JsonPropertyName("size")]
        [property: JsonRequired]
        long Size,
        [property: JsonPropertyName("size_vram")]
        [property: JsonRequired]
        long SizeVram,
        [property: JsonPropertyName("context_length")]
        [property: JsonRequired]
        int ContextLength,
        [property: JsonPropertyName("expires_at")]
        [property: JsonRequired]
        DateTimeOffset ExpiresAt);

    private sealed record PullRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record PullResponse(
        [property: JsonPropertyName("status")]
        [property: JsonRequired]
        string Status);

    /// <summary>
    /// One line of the pull stream. Every field is optional because the backend sends different
    /// shapes for different phases, and a required field would turn a manifest line into a
    /// failure.
    /// </summary>
    private sealed record PullStreamLine(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("total")] long? Total,
        [property: JsonPropertyName("completed")] long? Completed);

    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("keep_alive")] object KeepAlive,
        [property: JsonPropertyName("options")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        GenerateOptions? Options);

    private sealed record GenerateOptions(
        [property: JsonPropertyName("num_ctx")] int ContextTokens);

    private sealed record BoundedErrorBody(string Text, bool IsTruncated);
}
