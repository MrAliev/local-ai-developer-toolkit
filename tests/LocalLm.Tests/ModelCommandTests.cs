using LocalAi.Broker.Client;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class ModelCommandTests
{
    private static readonly DateTimeOffset VerifiedUtc =
        new(2026, 7, 31, 8, 9, 10, TimeSpan.Zero);

    [Fact]
    public async Task Status_calls_only_status_and_emits_one_canonical_response()
    {
        var client = new RecordingClient
        {
            StatusResult = Result(new LocalModelsStatusOutput(
                ["model-b", "model-a"],
                [],
                [],
                [],
                "catalog-7",
                PendingPullModels: ["model-c"])),
        };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteAsync(
            ["status"], client, output, TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.SuccessExitCode, exitCode);
        Assert.Equal(["status"], client.Calls);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":true," +
            "\"catalogVersion\":\"catalog-7\",\"installedModels\":[\"model-a\",\"model-b\"]," +
            "\"pendingPullModels\":[\"model-c\"]}" + Environment.NewLine,
            output.ToString());
    }

    [Fact]
    public async Task Pull_calls_only_pull_with_exact_model_and_catalog_version()
    {
        var client = new RecordingClient
        {
            PullResult = Result(new ModelMaintenanceJobOutput("success")),
        };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteAsync(
            ["pull", "--model", "qwen3.5:9b", "--catalog-version", "signed-7"],
            client,
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.SuccessExitCode, exitCode);
        Assert.Equal(["pull:qwen3.5:9b:signed-7"], client.Calls);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"pull\",\"accepted\":true," +
            "\"model\":\"qwen3.5:9b\",\"catalogVersion\":\"signed-7\",\"status\":\"success\"}" +
            Environment.NewLine,
            output.ToString());
    }

    [Fact]
    public async Task Preflight_calls_only_preflight_and_preserves_full_residency_proof()
    {
        var client = new RecordingClient
        {
            PreflightResult = Result(new LocalModelPreflightOutput(
                "qwen3.5:9b", 8192, "signed-7", 123, 123, true, VerifiedUtc)),
        };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteAsync(
            ["preflight", "--model", "qwen3.5:9b", "--context", "8192",
             "--catalog-version", "signed-7"],
            client,
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.SuccessExitCode, exitCode);
        Assert.Equal(["preflight:qwen3.5:9b:8192:signed-7"], client.Calls);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"preflight\",\"accepted\":true," +
            "\"model\":\"qwen3.5:9b\",\"contextTokens\":8192," +
            "\"catalogVersion\":\"signed-7\",\"sizeBytes\":123," +
            "\"sizeVramBytes\":123,\"fullyResident\":true," +
            "\"verifiedAtUtc\":\"2026-07-31T08:09:10+00:00\"}" + Environment.NewLine,
            output.ToString());
    }

    public static TheoryData<LocalModelPreflightOutput> InvalidPreflightProofs => new()
    {
        { new LocalModelPreflightOutput("other:9b", 8192, "signed-7", 123, 123, true, VerifiedUtc) },
        { new LocalModelPreflightOutput("qwen3.5:9b", 2048, "signed-7", 123, 123, true, VerifiedUtc) },
        { new LocalModelPreflightOutput("qwen3.5:9b", 8192, "stale", 123, 123, true, VerifiedUtc) },
        { new LocalModelPreflightOutput("qwen3.5:9b", 8192, "signed-7", 0, 0, true, VerifiedUtc) },
        { new LocalModelPreflightOutput("qwen3.5:9b", 8192, "signed-7", 123, 122, true, VerifiedUtc) },
        { new LocalModelPreflightOutput("qwen3.5:9b", 8192, "signed-7", 123, 123, false, VerifiedUtc) },
        { new LocalModelPreflightOutput("qwen3.5:9b", 8192, "signed-7", 123, 123, true, default) },
        { new LocalModelPreflightOutput("qwen3.5:9b", 8192, "signed-7", 123, 123, true,
            new DateTimeOffset(2026, 7, 31, 11, 9, 10, TimeSpan.FromHours(3))) },
    };

    [Theory]
    [MemberData(nameof(InvalidPreflightProofs))]
    public async Task Preflight_rejects_semantically_invalid_proof_before_accepted_true(
        LocalModelPreflightOutput proof)
    {
        var client = new RecordingClient { PreflightResult = Result(proof) };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteAsync(
            ["preflight", "--model", "qwen3.5:9b", "--context", "8192",
             "--catalog-version", "signed-7"],
            client,
            output,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(ModelCommand.SuccessExitCode, exitCode);
        Assert.DoesNotContain("\"accepted\":true", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(proof.Model == "other:9b" ? "other:9b" : "never-secret", output.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        { Array.Empty<string>() },
        { ["unknown"] },
        { ["status", "extra"] },
        { ["pull", "--model", "safe:1"] },
        { ["pull", "--model", "safe:1", "--model", "safe:2", "--catalog-version", "1"] },
        { ["pull", "--model", "../unsafe", "--catalog-version", "1"] },
        { ["pull", "--model", "safe:1", "--catalog-version", "../1"] },
        { ["pull", "--catalog-version", "1", "--model", "safe:1"] },
        { ["preflight", "--model", "safe:1", "--context", "0"] },
        { ["preflight", "--model", "safe:1", "--context", "2049"] },
        { ["preflight", "--model", "safe:1", "--context", "2,048"] },
        { ["preflight", "--model", "safe:1", "--context", "+2048"] },
        { ["preflight", "--model", "safe:1", "--context", "٢٠٤٨"] },
        { ["preflight", "--model", "safe:1", "--context", "2048", "extra"] },
        { ["preflight", "--model", "safe:1", "--context", "2048"] },
        { ["preflight", "--model", "safe:1", "--context", "2048", "--catalog-version", "../1"] },
        { ["preflight", "--catalog-version", "1", "--model", "safe:1", "--context", "2048"] },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task Invalid_grammar_is_rejected_before_any_client_call(string[] arguments)
    {
        var client = new RecordingClient();
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteAsync(
            arguments, client, output, TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.InvalidArgumentsExitCode, exitCode);
        Assert.Empty(client.Calls);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"invalid\",\"accepted\":false," +
            "\"code\":\"invalid_arguments\"}" + Environment.NewLine,
            output.ToString());
    }

    [Fact]
    public async Task Broker_preflight_failure_is_a_deterministic_rejection_without_raw_details()
    {
        var client = new RecordingClient
        {
            Failure = new BrokerJobFailedException(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "ModelPreflightException"),
        };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteAsync(
            ["preflight", "--model", "qwen3.5:9b", "--context", "2048",
             "--catalog-version", "signed-7"],
            client,
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.RejectedExitCode, exitCode);
        Assert.Equal(["preflight:qwen3.5:9b:2048:signed-7"], client.Calls);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"preflight\",\"accepted\":false," +
            "\"model\":\"qwen3.5:9b\",\"contextTokens\":2048," +
            "\"catalogVersion\":\"signed-7\"," +
            "\"code\":\"residency_rejected\"}" + Environment.NewLine,
            output.ToString());
        Assert.DoesNotContain("aaaaaaaa", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ModelPreflightException", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Broker_and_protocol_failures_are_sanitized()
    {
        var client = new RecordingClient
        {
            Failure = new BrokerProtocolException("secret pipe and raw response"),
        };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteAsync(
            ["status"], client, output, TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.FailureExitCode, exitCode);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":false," +
            "\"code\":\"broker_failure\"}" + Environment.NewLine,
            output.ToString());
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_has_a_distinct_sanitized_response_and_exit_code()
    {
        var client = new RecordingClient
        {
            Failure = new OperationCanceledException("secret cancellation detail"),
        };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteAsync(
            ["status"], client, output, TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.CancelledExitCode, exitCode);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":false," +
            "\"code\":\"cancelled\"}" + Environment.NewLine,
            output.ToString());
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_initialization_failure_emits_one_sanitized_json_response()
    {
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteProductionAsync(
            ["status"],
            _ => throw new InvalidOperationException("secret ACL path and runtime detail"),
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.FailureExitCode, exitCode);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":false," +
            "\"code\":\"broker_failure\"}" + Environment.NewLine,
            output.ToString());
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Production_initialization_cancellation_is_distinct_and_skips_factory_when_pre_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factoryCalled = false;
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteProductionAsync(
            ["preflight", "--model", "qwen3.5:9b", "--context", "2048",
             "--catalog-version", "signed-7"],
            token =>
            {
                factoryCalled = true;
                throw new OperationCanceledException(token);
            },
            output,
            cancellation.Token);

        Assert.Equal(ModelCommand.CancelledExitCode, exitCode);
        Assert.False(factoryCalled);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"preflight\",\"accepted\":false," +
            "\"code\":\"cancelled\"}" + Environment.NewLine,
            output.ToString());
    }

    [Fact]
    public async Task Production_factory_cancellation_is_sanitized()
    {
        var factoryCalled = false;
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteProductionAsync(
            ["pull", "--model", "qwen3.5:9b", "--catalog-version", "catalog-7"],
            token =>
            {
                factoryCalled = true;
                throw new OperationCanceledException("secret initialization detail", token);
            },
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCommand.CancelledExitCode, exitCode);
        Assert.True(factoryCalled);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"pull\",\"accepted\":false," +
            "\"code\":\"cancelled\"}" + Environment.NewLine,
            output.ToString());
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_boundary_passes_the_real_cancellation_token_to_factory_and_client()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken observedFactoryToken = default;
        var client = new RecordingClient
        {
            StatusResult = Result(new LocalModelsStatusOutput(
                [], [], [], [], "catalog-7")),
        };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteProductionAsync(
            ["status"],
            token =>
            {
                observedFactoryToken = token;
                return client;
            },
            output,
            cancellation.Token);

        Assert.Equal(ModelCommand.SuccessExitCode, exitCode);
        Assert.Equal(cancellation.Token, observedFactoryToken);
        Assert.Equal(["status"], client.Calls);
        Assert.Equal([cancellation.Token], client.Tokens);
    }

    [Fact]
    public async Task Cancellation_during_factory_initialization_stops_before_client_call()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingClient
        {
            StatusResult = Result(new LocalModelsStatusOutput(
                [], [], [], [], "catalog-7")),
        };
        using var output = new StringWriter();

        var exitCode = await ModelCommand.ExecuteProductionAsync(
            ["status"],
            _ =>
            {
                cancellation.Cancel();
                return client;
            },
            output,
            cancellation.Token);

        Assert.Equal(ModelCommand.CancelledExitCode, exitCode);
        Assert.Empty(client.Calls);
        Assert.Equal(
            "{\"schemaVersion\":1,\"operation\":\"status\",\"accepted\":false," +
            "\"code\":\"cancelled\"}" + Environment.NewLine,
            output.ToString());
    }

    private static LocalJobResult<T> Result<T>(T value) =>
        new(value, new LocalUsageReceipt(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "client", "operation", "model", TimeSpan.Zero, TimeSpan.Zero,
            0, 0, null, null, null));

    private sealed class RecordingClient : ILocalModelClient
    {
        public List<string> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public LocalJobResult<LocalModelsStatusOutput>? StatusResult { get; init; }
        public LocalJobResult<ModelMaintenanceJobOutput>? PullResult { get; init; }
        public LocalJobResult<LocalModelPreflightOutput>? PreflightResult { get; init; }
        public Exception? Failure { get; init; }

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default)
        {
            Calls.Add("status");
            Tokens.Add(cancellationToken);
            return Complete(StatusResult!);
        }

        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"pull:{model}:{catalogVersion}");
            Tokens.Add(cancellationToken);
            return Complete(PullResult!);
        }

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"preflight:{model}:{contextTokens}:{catalogVersion}");
            Tokens.Add(cancellationToken);
            return Complete(PreflightResult!);
        }

        private Task<T> Complete<T>(T result) =>
            Failure is null ? Task.FromResult(result) : Task.FromException<T>(Failure);

        public Task<LocalJobResult<string>> ChatAsync(
            string model, string prompt, string? system,
            IReadOnlyList<string>? imagesBase64, LocalJobPriority priority,
            CancellationToken cancellationToken = default) => Forbidden<LocalJobResult<string>>();

        public Task<LocalJobResult<string>> RoutedChatAsync(
            LocalTaskProfile profile, string prompt, string? system,
            IReadOnlyList<string>? imagesBase64, LocalWorkloadMetadata workload,
            LocalWorkflowHint? workflow, string? modelOverride,
            int? requestedContextTokens, LocalJobPriority priority,
            CancellationToken cancellationToken = default) => Forbidden<LocalJobResult<string>>();

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) => Forbidden<LocalJobResult<IReadOnlyList<string>>>();

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile, string model,
            CancellationToken cancellationToken = default) => Forbidden<LocalJobResult<LocalExperimentReportOutput>>();

        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile, string model, ExperimentOwnerAction action,
            CancellationToken cancellationToken = default) => Forbidden<LocalJobResult<LocalModelFeedbackOutput>>();

        private static Task<T> Forbidden<T>() =>
            Task.FromException<T>(new InvalidOperationException("Forbidden client API."));
    }
}
