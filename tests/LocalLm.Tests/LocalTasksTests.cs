using System.Buffers.Binary;
using LocalAi.Contracts;
using LocalAi.Broker.Client;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class LocalTasksTests
{
    [Fact]
    public async Task Log_triage_uses_fixed_profile_and_never_the_stale_27b_default()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path,
                "error CS1000",
                TestContext.Current.CancellationToken);
            var client = new FakeLocalModelClient();
            var tasks = new LocalTasks(client);

            await tasks.TriageLogAsync(
                path,
                null,
                model: null,
                TestContext.Current.CancellationToken);

            var call = Assert.Single(client.Calls);
            Assert.Equal(LocalTaskProfile.LogTriage, call.Profile);
            Assert.Null(call.ModelOverride);
            Assert.DoesNotContain("qwen3.6:27b", call.Prompt, StringComparison.Ordinal);
            Assert.InRange(call.RequestedContextTokens!.Value, 2048, 262144);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Ask_rejects_translation_profiles_that_bypass_validation()
    {
        var client = new FakeLocalModelClient();
        var tasks = new LocalTasks(client);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tasks.AskAsync(
                LocalTaskProfile.PlainTranslation,
                "translate",
                [],
                model: null,
                TestContext.Current.CancellationToken));

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Image_translation_completes_one_experiment_workflow()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"localai-image-translation-{Guid.NewGuid():N}.png");
        var header = new byte[32];
        header[0] = 0x89;
        header[1] = (byte)'P';
        header[2] = (byte)'N';
        header[3] = (byte)'G';
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16, 4), 100);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), 80);
        try
        {
            await File.WriteAllBytesAsync(
                path,
                header,
                TestContext.Current.CancellationToken);
            var client = new FakeLocalModelClient
            {
                Answer = "Переведённый текст"
            };
            var tasks = new LocalTasks(client);

            await tasks.ReadImageAsync(
                [path],
                "Translate all visible text into Russian.",
                LocalTaskProfile.ImageTranslation,
                model: null,
                TestContext.Current.CancellationToken);

            var call = Assert.Single(client.Calls);
            Assert.Equal(LocalTaskProfile.ImageTranslation, call.Profile);
            Assert.Contains(
                "Translate",
                call.System,
                StringComparison.Ordinal);
            Assert.NotNull(call.WorkflowId);
            var completion = Assert.Single(client.ExperimentCompletions);
            Assert.Equal(call.WorkflowId, completion.WorkflowId);
            Assert.Equal(LocalTaskProfile.ImageTranslation, completion.Profile);
            Assert.Equal(ModelExecutionOutcome.Success, completion.Outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Technical_translation_chunks_validates_and_attributes_actual_model()
    {
        var client = new FakeLocalModelClient
        {
            Answer = "# Перевод\r\n\r\nТекст с `Code()`."
        };
        var tasks = new LocalTasks(client);

        var result = await tasks.TranslateAsync(
            "# Source\r\n\r\nText with `Code()`.",
            "English",
            "Russian",
            markdown: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalTaskProfile.TechnicalTranslation, Assert.Single(client.Calls).Profile);
        Assert.Contains(
            "Перевод выполнен локальной моделью: translategemma:12b.",
            result.Answer,
            StringComparison.Ordinal);
        Assert.True(result.Validation.Passed);
    }

    [Fact]
    public async Task Structural_failure_retries_with_established_technical_fallback()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("# Перевод\r\n\r\nТекст без защищённого токена.");
        client.Answers.Enqueue("# Перевод\r\n\r\nТекст с `Code()`.");
        var tasks = new LocalTasks(client);

        var result = await tasks.TranslateAsync(
            "# Source\r\n\r\nText with `Code()`.",
            "English",
            "Russian",
            markdown: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Calls.Count);
        Assert.Null(client.Calls[0].ModelOverride);
        Assert.Equal("qwen2.5-coder:14b", client.Calls[1].ModelOverride);
        Assert.Equal("qwen2.5-coder:14b", result.Model);
        Assert.Contains(
            "Перевод выполнен локальной моделью: qwen2.5-coder:14b.",
            result.Answer,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_translation_validation_failure_retries_with_established_fallback()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue(
            "Translate the following fragment from English to Russian.");
        client.Answers.Enqueue("Английская версия");
        var tasks = new LocalTasks(client);

        var result = await tasks.TranslateAsync(
            "English version",
            "English",
            "Russian",
            markdown: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Calls.Count);
        Assert.Null(client.Calls[0].ModelOverride);
        Assert.Equal("qwen3.5:9b", client.Calls[1].ModelOverride);
        Assert.Equal("qwen3.5:9b", result.Model);
        Assert.True(result.Validation.Passed, result.Validation.Detail);
    }

    [Fact]
    public async Task Technical_translation_tries_the_next_established_fallback()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue(
            "# Перевод\r\n\r\nТекст без защищённого токена.");
        client.Answers.Enqueue("# Перевод\r\n\r\nТекст с `Code()`.");
        client.UnavailableOverrides.Add("qwen2.5-coder:14b");
        var tasks = new LocalTasks(client);

        var result = await tasks.TranslateAsync(
            "# Source\r\n\r\nText with `Code()`.",
            "English",
            "Russian",
            markdown: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, client.Calls.Count);
        Assert.Null(client.Calls[0].ModelOverride);
        Assert.Equal("qwen2.5-coder:14b", client.Calls[1].ModelOverride);
        Assert.Equal("qwen3.5:9b", client.Calls[2].ModelOverride);
        Assert.Equal("qwen3.5:9b", result.Model);
        Assert.True(result.Validation.Passed, result.Validation.Detail);
    }

    [Fact]
    public async Task Translation_context_accounts_for_source_and_expected_output()
    {
        var client = new FakeLocalModelClient
        {
            Answer = "Перевод"
        };
        var tasks = new LocalTasks(client);

        await tasks.TranslateAsync(
            new string('a', 6_000),
            "English",
            "Russian",
            markdown: false,
            TestContext.Current.CancellationToken);

        Assert.InRange(
            Assert.Single(client.Calls).RequestedContextTokens!.Value,
            8192,
            262144);
    }

    [Fact]
    public async Task Technical_translation_preserves_whitespace_around_fenced_code()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("# Перевод");
        client.Answers.Enqueue("После блока");
        var tasks = new LocalTasks(client);
        const string source =
            "# Source\r\n\r\n" +
            "```text\r\n" +
            "value\r\n" +
            "```\r\n\r\n" +
            "After block";

        var result = await tasks.TranslateAsync(
            source,
            "English",
            "Russian",
            markdown: true,
            TestContext.Current.CancellationToken);

        Assert.StartsWith(
            "# Перевод\r\n\r\n```text\r\nvalue\r\n```\r\n\r\nПосле блока",
            result.Answer,
            StringComparison.Ordinal);
        Assert.True(result.Validation.Passed, result.Validation.Detail);
    }

    [Fact]
    public async Task Chunked_translation_completes_one_experiment_workflow()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("# Перевод");
        client.Answers.Enqueue("После блока");
        var tasks = new LocalTasks(client);
        const string source =
            "# Source\r\n\r\n" +
            "```text\r\nvalue\r\n```\r\n\r\n" +
            "After block";

        await tasks.TranslateAsync(
            source,
            "English",
            "Russian",
            markdown: true,
            TestContext.Current.CancellationToken);

        var completion = Assert.Single(client.ExperimentCompletions);
        Assert.Equal(
            LocalTaskProfile.TechnicalTranslation,
            completion.Profile);
        Assert.Equal("translategemma:12b", completion.Model);
        Assert.Equal(ModelExecutionOutcome.Success, completion.Outcome);
        Assert.False(completion.Metrics.UsedFallback);
        Assert.Equal(2, client.Calls.Count);
        Assert.Single(client.Calls.Select(call => call.WorkflowId).Distinct());
    }

    [Fact]
    public async Task Broker_fallback_completes_workflow_as_one_technical_failure()
    {
        var client = new FakeLocalModelClient
        {
            Answer = "Перевод",
            BrokerFailureOutcome = ModelExecutionOutcome.TechnicalFailure
        };
        var tasks = new LocalTasks(client);

        await tasks.TranslateAsync(
            "Translation",
            "English",
            "Russian",
            markdown: false,
            TestContext.Current.CancellationToken);

        var completion = Assert.Single(client.ExperimentCompletions);
        Assert.Equal("translategemma:12b", completion.Model);
        Assert.Equal(
            ModelExecutionOutcome.TechnicalFailure,
            completion.Outcome);
        Assert.True(completion.Metrics.UsedFallback);
    }

    private sealed class FakeLocalModelClient : ILocalModelClient
    {
        public string Answer { get; init; } = "answer";

        public ModelExecutionOutcome? BrokerFailureOutcome { get; init; }

        public Queue<string> Answers { get; } = [];

        public HashSet<string> UnavailableOverrides { get; } =
            new(StringComparer.Ordinal);

        public List<RoutedCall> Calls { get; } = [];

        public List<ExperimentCompletion> ExperimentCompletions { get; } = [];

        public Task<LocalJobResult<string>> ChatAsync(
            string model,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<string>> RoutedChatAsync(
            LocalTaskProfile profile,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalWorkloadMetadata workload,
            LocalWorkflowHint? workflow,
            string? modelOverride,
            int? requestedContextTokens,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(
                new RoutedCall(
                    profile,
                    prompt,
                    system,
                    modelOverride,
                    requestedContextTokens,
                    workflow?.WorkflowId));
            if (modelOverride is not null &&
                UnavailableOverrides.Contains(modelOverride))
            {
                throw new BrokerJobFailedException(
                    Guid.NewGuid(),
                    nameof(InvalidOperationException));
            }

            var answer = Answers.Count == 0 ? Answer : Answers.Dequeue();
            var brokerFallback =
                modelOverride is null &&
                BrokerFailureOutcome is not null;
            var selectedModel = modelOverride ??
                                (brokerFallback
                                    ? "qwen3.5:9b"
                                    : "translategemma:12b");
            return Task.FromResult(new LocalJobResult<string>(
                answer,
                new LocalUsageReceipt(
                    Guid.NewGuid(),
                    "local-lm",
                    "chat",
                    modelOverride ?? "translategemma:12b",
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    prompt.Length,
                    prompt.Length / 4,
                    null,
                    null,
                    null,
                    new LocalRoutingReceipt(
                        profile,
                        selectedModel,
                        requestedContextTokens,
                        WasCold: Calls.Count == 1,
                        UsedFallback: modelOverride is not null || brokerFallback,
                        ValidatorResult: "none:pass",
                        EstimatedGrossCloudTokensSaved: prompt.Length / 4,
                        EstimatedVerificationTokens: 0,
                        EstimatedNetCloudTokensSaved: prompt.Length / 4,
                        IsExperimentalAttempt:
                            modelOverride is null && !brokerFallback,
                        ExperimentalModel:
                            brokerFallback ? "translategemma:12b" : null,
                        ExperimentalOutcome:
                            brokerFallback ? BrokerFailureOutcome : null))));
        }

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalExperimentCompletionOutput>>
            CompleteExperimentAsync(
                Guid workflowId,
                LocalTaskProfile profile,
                string model,
                ModelExecutionOutcome outcome,
                LocalExperimentTaskMetrics metrics,
                CancellationToken cancellationToken = default)
        {
            ExperimentCompletions.Add(
                new ExperimentCompletion(
                    workflowId,
                    profile,
                    model,
                    outcome,
                    metrics));
            var output = new LocalExperimentCompletionOutput(
                workflowId,
                profile,
                model,
                outcome);
            return Task.FromResult(
                new LocalJobResult<LocalExperimentCompletionOutput>(
                    output,
                    new LocalUsageReceipt(
                        Guid.NewGuid(),
                        "local-lm",
                        "experiment-completion",
                        model,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        0,
                        0,
                        null,
                        null,
                        null)));
        }
    }

    private sealed record RoutedCall(
        LocalTaskProfile Profile,
        string Prompt,
        string? System,
        string? ModelOverride,
        int? RequestedContextTokens,
        Guid? WorkflowId);

    private sealed record ExperimentCompletion(
        Guid WorkflowId,
        LocalTaskProfile Profile,
        string Model,
        ModelExecutionOutcome Outcome,
        LocalExperimentTaskMetrics Metrics);
}
