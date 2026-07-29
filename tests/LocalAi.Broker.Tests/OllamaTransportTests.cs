using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class OllamaTransportTests
{
    private static readonly Uri BaseUri = new("http://ollama.test:11434/");

    [Fact]
    public async Task Runtime_processes_map_full_vram_and_context_fields()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "models": [{
                "name": "translategemma:12b",
                "size": 8109818272,
                "size_vram": 8109818272,
                "context_length": 2048,
                "expires_at": "2026-07-29T05:00:00+03:00"
              }]
            }
            """);
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var processes = await transport.ListProcessesAsync(
            TestContext.Current.CancellationToken);

        var process = Assert.Single(processes);
        Assert.Equal("translategemma:12b", process.Model);
        Assert.Equal(8109818272, process.SizeBytes);
        Assert.Equal(process.SizeBytes, process.SizeVramBytes);
        Assert.Equal(2048, process.ContextTokens);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 29, 2, 0, 0, TimeSpan.Zero),
            process.ExpiresAtUtc);
        Assert.Equal(new Uri(BaseUri, "api/ps"), Assert.Single(fake.Requests).Uri);
    }

    [Fact]
    public async Task Runtime_pull_posts_non_streaming_request()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"status":"success"}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await transport.PullAsync(
            "translategemma:12b",
            TestContext.Current.CancellationToken);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(BaseUri, "api/pull"), request.Uri);
        Assert.Equal(
            """{"model":"translategemma:12b","stream":false}""",
            request.Body);
    }

    [Fact]
    public async Task Runtime_preflight_uses_empty_prompt_selected_context_and_bounded_residency()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"response":"","done":true}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await transport.PreflightAsync(
            "translategemma:12b",
            2048,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(new Uri(BaseUri, "api/generate"), request.Uri);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("translategemma:12b", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(string.Empty, body.RootElement.GetProperty("prompt").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("30m", body.RootElement.GetProperty("keep_alive").GetString());
        Assert.Equal(
            2048,
            body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Runtime_unload_sets_keep_alive_to_zero()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"response":"","done":true}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await transport.UnloadAsync(
            "translategemma:12b",
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(Assert.Single(fake.Requests).Body!);
        Assert.Equal(0, body.RootElement.GetProperty("keep_alive").GetInt32());
    }

    [Fact]
    public async Task Native_chat_preserves_tool_calls_and_usage_metadata()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "message": {
                "role": "assistant",
                "content": "",
                "tool_calls": [{"function":{"name":"read_file","arguments":{"path":"a.cs"}}}]
              },
              "done": true,
              "prompt_eval_count": 12,
              "eval_count": 3,
              "total_duration": 99
            }
            """);
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);
        var requestBody = JsonSerializer.SerializeToElement(new
        {
            model = "agent-model",
            messages = new[] { new { role = "user", content = "inspect" } },
            tools = new[] { new { type = "function" } },
            stream = false
        });

        var result = await transport.ExecuteAsync(
            LocalJobRequestFactory.CreateNativeOllama(
                "native-chat",
                LocalJobPriority.Foreground,
                NativeOllamaOperation.Chat,
                requestBody),
            TestContext.Current.CancellationToken);

        var output = result.Body.Deserialize<NativeOllamaJobOutput>(
            LocalAiJson.Strict);
        Assert.NotNull(output);
        Assert.Equal(
            "read_file",
            output.Response
                .GetProperty("message")
                .GetProperty("tool_calls")[0]
                .GetProperty("function")
                .GetProperty("name")
                .GetString());
        Assert.Equal(new Uri(BaseUri, "api/chat"), Assert.Single(fake.Requests).Uri);
    }

    [Fact]
    public async Task Native_processes_uses_allowlisted_get_endpoint()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"models":[]}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await transport.ExecuteAsync(
            LocalJobRequestFactory.CreateNativeOllama(
                "native-ps",
                LocalJobPriority.Interactive,
                NativeOllamaOperation.Processes,
                null),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri(BaseUri, "api/ps"), request.Uri);
    }

    [Fact]
    public async Task Embed_posts_exact_request_and_maps_defensive_strict_output()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"embeddings":[[3,4],[0.25,-2]]}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var result = await transport.ExecuteAsync(
            LocalJobRequestFactory.CreateEmbed(
                "embed-key",
                LocalJobPriority.Foreground,
                "embed-model",
                ["INPUT_SECRET_ONE", "second"]),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(BaseUri, "api/embed"), request.Uri);
        Assert.Equal(
            """{"model":"embed-model","input":["INPUT_SECRET_ONE","second"]}""",
            request.Body);
        var output = result.Body.Deserialize<EmbedJobOutput>(LocalAiJson.Strict);
        Assert.NotNull(output);
        Assert.Equal([3d, 4d], output.Embeddings[0]);
        Assert.Equal([0.25d, -2d], output.Embeddings[1]);
        Assert.Throws<NotSupportedException>(
            () => ((IList<IReadOnlyList<double>>)output.Embeddings).Add([1d]));
        Assert.Throws<NotSupportedException>(
            () => ((IList<double>)output.Embeddings[0]).Add(1d));
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<EmbedJobOutput>(
                """{"Embeddings":[[1]],"Unexpected":true}""",
            LocalAiJson.Strict));
    }

    [Fact]
    public async Task Embed_accepts_official_metadata_and_forward_compatible_fields()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "model": "embeddinggemma",
              "embeddings": [[0.1, 0.2]],
              "total_duration": 14143917,
              "load_duration": 1019500,
              "prompt_eval_count": 8,
              "future_metadata": {"version": 2}
            }
            """);
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var result = await transport.ExecuteAsync(
            LocalJobRequestFactory.CreateEmbed(
                "embed-official",
                LocalJobPriority.Foreground,
                "embeddinggemma",
                ["input"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [0.1d, 0.2d],
            result.Body.Deserialize<EmbedJobOutput>(LocalAiJson.Strict)!.Embeddings[0]);
    }

    [Fact]
    public async Task Chat_posts_system_first_and_user_images_then_maps_trimmed_content()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"  answer  "}}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var result = await transport.ExecuteAsync(
            LocalJobRequestFactory.CreateChat(
                "chat-key",
                LocalJobPriority.Interactive,
                "chat-model",
                "PROMPT_SECRET",
                "SYSTEM_SECRET",
                ["IMAGE_SECRET"]),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(BaseUri, "api/chat"), request.Uri);
        Assert.Equal(
            """{"model":"chat-model","messages":[{"role":"system","content":"SYSTEM_SECRET"},{"role":"user","content":"PROMPT_SECRET","images":["IMAGE_SECRET"]}],"stream":false}""",
            request.Body);
        Assert.Equal(
            "answer",
            result.Body.Deserialize<ChatJobOutput>(LocalAiJson.Strict)!.Content);
    }

    [Fact]
    public async Task Routed_chat_reuses_the_preflight_context_on_the_real_request()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"answer"}}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);
        var routed = LocalJobRequestFactory.CreateRoutedChat(
            "routed-chat",
            LocalJobPriority.Foreground,
            LocalTaskProfile.PlainTranslation,
            "translate",
            null,
            null,
            new LocalWorkloadMetadata(
                9,
                20,
                0,
                0,
                0,
                LocalDurationClass.Short),
            requestedContextTokens: 2048);

        await transport.ExecuteAsync(
            LocalJobRequestFactory.ResolveRoutedChat(
                routed,
                "translategemma:12b"),
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(Assert.Single(fake.Requests).Body!);
        Assert.Equal(
            2048,
            body.RootElement
                .GetProperty("options")
                .GetProperty("num_ctx")
                .GetInt32());
    }

    [Fact]
    public async Task Chat_without_optional_values_omits_system_message_and_images()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"answer"}}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await transport.ExecuteAsync(
            LocalJobRequestFactory.CreateChat(
                "chat-key",
                LocalJobPriority.Interactive,
                "chat-model",
                "prompt",
                null,
                []),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"model":"chat-model","messages":[{"role":"user","content":"prompt"}],"stream":false}""",
            Assert.Single(fake.Requests).Body);
    }

    [Fact]
    public async Task Chat_accepts_official_metadata_and_forward_compatible_fields()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "model": "gemma3",
              "created_at": "2025-01-01T00:00:00Z",
              "message": {
                "role": "assistant",
                "content": "  answer  ",
                "images": null,
                "tool_calls": [],
                "future_message_metadata": true
              },
              "done": true,
              "done_reason": "stop",
              "total_duration": 5191566416,
              "load_duration": 2154458,
              "prompt_eval_count": 26,
              "prompt_eval_duration": 383809000,
              "eval_count": 298,
              "eval_duration": 4799921000,
              "future_metadata": {"version": 2}
            }
            """);
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var result = await transport.ExecuteAsync(
            ChatRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "answer",
            result.Body.Deserialize<ChatJobOutput>(LocalAiJson.Strict)!.Content);
    }

    [Fact]
    public async Task ListModels_gets_tags_and_preserves_order_with_ordinal_deduplication()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.OK,
            """{"models":[{"name":"alpha"},{"name":"alpha"},{"name":"Alpha"},{"name":"beta"}]}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var result = await transport.ExecuteAsync(
            LocalJobRequestFactory.CreateListModels(
                "tags-key",
                LocalJobPriority.Background),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri(BaseUri, "api/tags"), request.Uri);
        Assert.Null(request.Body);
        var output = result.Body.Deserialize<ListModelsJobOutput>(LocalAiJson.Strict);
        Assert.Equal(["alpha", "Alpha", "beta"], output!.Models);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)output.Models).Add("later"));
    }

    [Fact]
    public async Task Tags_accepts_official_details_and_forward_compatible_fields()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "models": [{
                "name": "gemma3:latest",
                "model": "gemma3:latest",
                "modified_at": "2025-01-01T00:00:00Z",
                "size": 3338801804,
                "digest": "abc123",
                "details": {
                  "parent_model": "",
                  "format": "gguf",
                  "family": "gemma3",
                  "families": ["gemma3"],
                  "parameter_size": "4.3B",
                  "quantization_level": "Q4_K_M",
                  "future_detail": true
                },
                "future_model_metadata": 2
              }],
              "future_metadata": {"version": 2}
            }
            """);
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var result = await transport.ExecuteAsync(
            LocalJobRequestFactory.CreateListModels(
                "tags-official",
                LocalJobPriority.Background),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["gemma3:latest"],
            result.Body.Deserialize<ListModelsJobOutput>(LocalAiJson.Strict)!.Models);
    }

    [Fact]
    public async Task External_response_rejects_duplicate_json_properties()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.OK,
            """{"message":{"content":"first","content":"second"}}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => transport.ExecuteAsync(
                ChatRequest(),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"embeddings":[[1,2]]}""")]
    [InlineData("""{"embeddings":[[],[]]}""")]
    [InlineData("""{"embeddings":[[1],[1,2]]}""")]
    [InlineData("""{"embeddings":[[1,null],[2,3]]}""")]
    [InlineData("""{"embeddings":[[1e999],[2]]}""")]
    [InlineData("""{"embeddings":null}""")]
    [InlineData("""{}""")]
    public async Task Embed_rejects_invalid_success_payload_without_retry(string responseJson)
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, responseJson);
        fake.EnqueueJson(HttpStatusCode.OK, """{"embeddings":[[1],[2]]}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await Assert.ThrowsAnyAsync<Exception>(
            () => transport.ExecuteAsync(
                LocalJobRequestFactory.CreateEmbed(
                    "embed-key",
                    LocalJobPriority.Foreground,
                    "model",
                    ["one", "two"]),
                TestContext.Current.CancellationToken));

        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData("""{"message":{"content":""}}""")]
    [InlineData("""{"message":{"content":" "}}""")]
    [InlineData("""{"message":null}""")]
    [InlineData("""{}""")]
    [InlineData("""not-json""")]
    public async Task Chat_rejects_empty_or_malformed_success_without_retry(string responseJson)
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, responseJson);
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"later"}}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await Assert.ThrowsAnyAsync<Exception>(
            () => transport.ExecuteAsync(ChatRequest(), TestContext.Current.CancellationToken));

        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData("""{"models":[{"name":""}]}""")]
    [InlineData("""{"models":[{"name":" "}]}""")]
    [InlineData("""{"models":null}""")]
    [InlineData("""{}""")]
    public async Task Tags_rejects_blank_or_malformed_models(string responseJson)
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(HttpStatusCode.OK, responseJson);
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        await Assert.ThrowsAnyAsync<Exception>(
            () => transport.ExecuteAsync(
                LocalJobRequestFactory.CreateListModels(
                    "tags",
                    LocalJobPriority.Background),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retries_transient_exception_408_429_and_500_then_succeeds()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueException(new HttpRequestException("temporary"));
        fake.EnqueueJson(HttpStatusCode.RequestTimeout, """{"error":"wait"}""");
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"answer"}}""");
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(
            client,
            BaseUri,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await transport.ExecuteAsync(
            ChatRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal("answer", result.Body.GetProperty("Content").GetString());
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
    }

    [Fact]
    public async Task Retries_direct_io_transport_exception_then_succeeds()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueException(new IOException("temporary"));
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"answer"}}""");
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(
            client,
            BaseUri,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await transport.ExecuteAsync(ChatRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Retries_retryable_status_at_most_three_attempts(HttpStatusCode statusCode)
    {
        var fake = new FakeOllamaServer();
        for (var index = 0; index < 3; index++)
        {
            fake.EnqueueJson(statusCode, """{"error":"temporary"}""");
        }

        var delays = new List<TimeSpan>();
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(
            client,
            BaseUri,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => transport.ExecuteAsync(ChatRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Retryable_status_then_success_retries_once(HttpStatusCode statusCode)
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(statusCode, """{"error":"temporary"}""");
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"answer"}}""");
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(
            client,
            BaseUri,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await transport.ExecuteAsync(ChatRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task RetryAfter_delta_overrides_deterministic_delay()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.TooManyRequests,
            """{"error":"wait"}""",
            response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"answer"}}""");
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(
            client,
            BaseUri,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await transport.ExecuteAsync(ChatRequest(), TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task RetryAfter_http_date_uses_bounded_server_delay()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.TooManyRequests,
            """{"error":"wait"}""",
            response => response.Headers.RetryAfter = new RetryConditionHeaderValue(
                DateTimeOffset.UtcNow.AddSeconds(20)));
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"answer"}}""");
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(
            client,
            BaseUri,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await transport.ExecuteAsync(ChatRequest(), TestContext.Current.CancellationToken);

        var delay = Assert.Single(delays);
        Assert.InRange(delay, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Unbounded_retry_after_falls_back_to_deterministic_delay()
    {
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.ServiceUnavailable,
            """{"error":"wait"}""",
            response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(2)));
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"answer"}}""");
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(
            client,
            BaseUri,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await transport.ExecuteAsync(ChatRequest(), TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Bad_request_is_not_retried_and_error_is_bounded_and_secret_free()
    {
        var marker = new string('x', 600);
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.BadRequest,
            $$"""{"error":"PROMPT_SECRET SYSTEM_SECRET IMAGE_SECRET {{marker}}"}""");
        fake.EnqueueJson(HttpStatusCode.OK, """{"message":{"content":"later"}}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => transport.ExecuteAsync(
                LocalJobRequestFactory.CreateChat(
                    "chat",
                    LocalJobPriority.Interactive,
                    "model",
                    "PROMPT_SECRET",
                    "SYSTEM_SECRET",
                    ["IMAGE_SECRET"]),
                TestContext.Current.CancellationToken));

        Assert.Single(fake.Requests);
        Assert.True(exception.Message.Length < 550);
        Assert.DoesNotContain("PROMPT_SECRET", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSTEM_SECRET", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("IMAGE_SECRET", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_redaction_does_not_leak_long_secret_prefix_or_suffix()
    {
        var secret = $"SECRET_PREFIX_{new string('x', 500)}_SECRET_SUFFIX";
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.BadRequest,
            $$"""{"error":"{{secret}}"}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => transport.ExecuteAsync(
                LocalJobRequestFactory.CreateEmbed(
                    "embed-secret",
                    LocalJobPriority.Foreground,
                    "model",
                    [secret]),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain("SECRET_PREFIX", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_SUFFIX", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.Length < 550);
    }

    [Fact]
    public async Task Error_redaction_handles_overlapping_secrets_longest_first_without_leaks()
    {
        const string shorterSecret = "OVERLAP_SECRET";
        const string longerSecret = "OVERLAP_SECRET_SUFFIX";
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.BadRequest,
            $$"""{"error":"{{longerSecret}}"}""");
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => transport.ExecuteAsync(
                LocalJobRequestFactory.CreateChat(
                    "chat-secret",
                    LocalJobPriority.Interactive,
                    "model",
                    shorterSecret,
                    longerSecret,
                    []),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain("OVERLAP_SECRET", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SUFFIX", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_redaction_clamps_expanded_sanitized_excerpt_to_400_characters()
    {
        const string sensitiveMarker = "x";
        var fake = new FakeOllamaServer();
        fake.EnqueueJson(
            HttpStatusCode.BadRequest,
            new string(sensitiveMarker[0], 400));
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => transport.ExecuteAsync(
                LocalJobRequestFactory.CreateChat(
                    "chat-secret",
                    LocalJobPriority.Interactive,
                    "model",
                    sensitiveMarker,
                    null,
                    []),
                TestContext.Current.CancellationToken));

        const string responsePrefix = " Response: ";
        var excerptStart = exception.Message.IndexOf(
            responsePrefix,
            StringComparison.Ordinal);
        Assert.True(excerptStart >= 0);
        var excerpt = exception.Message[(excerptStart + responsePrefix.Length)..];
        Assert.True(excerpt.Length <= 400);
        Assert.DoesNotContain(sensitiveMarker, excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_retry_or_wrapping()
    {
        var fake = new FakeOllamaServer();
        fake.Enqueue(
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable.");
            });
        using var client = new HttpClient(fake);
        using var transport = new OllamaTransport(client, BaseUri, NoDelay);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExecuteAsync(ChatRequest(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void Disposing_transport_does_not_dispose_injected_client()
    {
        var fake = new FakeOllamaServer();
        var client = new HttpClient(fake);

        new OllamaTransport(client, BaseUri, NoDelay).Dispose();

        Assert.False(fake.IsDisposed);
        client.Dispose();
        Assert.True(fake.IsDisposed);
    }

    private static LocalJobRequest ChatRequest() =>
        LocalJobRequestFactory.CreateChat(
            "chat-key",
            LocalJobPriority.Interactive,
            "chat-model",
            "prompt",
            null,
            []);

    private static Task NoDelay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
