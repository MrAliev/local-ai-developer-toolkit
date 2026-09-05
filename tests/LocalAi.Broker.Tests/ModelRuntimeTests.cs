using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelRuntimeTests
{
    [Fact]
    public async Task Ensure_ready_returns_proof_only_for_a_fully_resident_process()
    {
        var transport = new FakeRuntimeTransport
        {
            Installed = ["translategemma:12b"],
            Processes =
            [
                new OllamaProcessInfo(
                    "translategemma:12b",
                    8109818272,
                    8109818272,
                    2048,
                    DateTimeOffset.UtcNow.AddMinutes(30))
            ]
        };
        var runtime = new ModelRuntime(
            transport,
            ModelRoutingCatalog.LoadEmbedded(),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 29, 1, 0, 0, TimeSpan.Zero)));

        var proof = await runtime.EnsureReadyAsync(
            "translategemma:12b",
            2048,
            TestContext.Current.CancellationToken);

        Assert.True(proof.FullyResident);
        Assert.Equal(proof.SizeBytes, proof.SizeVramBytes);
        Assert.Equal(2048, proof.ContextTokens);
        Assert.Equal(["translategemma:12b:2048"], transport.Preflights);
        Assert.Empty(transport.Unloaded);
    }

    [Fact]
    public async Task Ensure_ready_uses_embedding_preflight_for_embedding_only_model()
    {
        const string model = "qwen3-embedding:8b-q8_0";
        var transport = new FakeRuntimeTransport
        {
            Installed = [model],
            RejectEmbeddingGenerationPreflight = true,
            Processes =
            [
                new OllamaProcessInfo(
                    model,
                    11800223415,
                    11800223415,
                    2048,
                    DateTimeOffset.UtcNow.AddMinutes(30))
            ]
        };
        var runtime = new ModelRuntime(
            transport,
            ModelRoutingCatalog.LoadEmbedded());

        var proof = await runtime.EnsureReadyAsync(
            model,
            2048,
            TestContext.Current.CancellationToken);

        Assert.True(proof.FullyResident);
        Assert.Equal([model + ":2048"], transport.EmbeddingPreflights);
        Assert.Empty(transport.Preflights);
        Assert.False(runtime.IsDisabled(model, 2048));
    }

    [Fact]
    public async Task Ensure_ready_unloads_and_disables_partial_vram_residency()
    {
        var transport = new FakeRuntimeTransport
        {
            Installed = ["gpt-oss:20b"],
            Processes =
            [
                new OllamaProcessInfo(
                    "gpt-oss:20b",
                    13000000000,
                    11000000000,
                    16384,
                    DateTimeOffset.UtcNow.AddMinutes(30))
            ]
        };
        var runtime = new ModelRuntime(
            transport,
            ModelRoutingCatalog.LoadEmbedded());

        var exception = await Assert.ThrowsAsync<ModelPreflightException>(
            () => runtime.EnsureReadyAsync(
                "gpt-oss:20b",
                16384,
                TestContext.Current.CancellationToken));

        Assert.Equal(ModelExecutionOutcome.CpuOffload, exception.Outcome);
        Assert.Contains("fully resident", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["gpt-oss:20b"], transport.Unloaded);
        Assert.True(runtime.IsDisabled("gpt-oss:20b", 16384));
        Assert.False(runtime.IsDisabled("gpt-oss:20b", 8192));
    }

    [Fact]
    public async Task Ensure_ready_unloads_and_disables_a_missing_process_entry()
    {
        var transport = new FakeRuntimeTransport
        {
            Installed = ["translategemma:12b"],
            Processes = []
        };
        var runtime = new ModelRuntime(
            transport,
            ModelRoutingCatalog.LoadEmbedded());

        await Assert.ThrowsAsync<ModelPreflightException>(
            () => runtime.EnsureReadyAsync(
                "translategemma:12b",
                2048,
                TestContext.Current.CancellationToken));

        Assert.Equal(["translategemma:12b"], transport.Unloaded);
        Assert.True(runtime.IsDisabled("translategemma:12b", 2048));
    }

    [Fact]
    public async Task Ensure_ready_reloads_an_existing_runner_with_the_requested_context()
    {
        var transport = new FakeRuntimeTransport
        {
            Installed = ["translategemma:12b"]
        };
        transport.ProcessSnapshots.Enqueue(
        [
            new OllamaProcessInfo(
                "translategemma:12b",
                8109818272,
                8109818272,
                16384,
                DateTimeOffset.UtcNow.AddMinutes(30))
        ]);
        transport.ProcessSnapshots.Enqueue(
        [
            new OllamaProcessInfo(
                "translategemma:12b",
                8109818272,
                8109818272,
                2048,
                DateTimeOffset.UtcNow.AddMinutes(30))
        ]);
        var runtime = new ModelRuntime(
            transport,
            ModelRoutingCatalog.LoadEmbedded());

        var proof = await runtime.EnsureReadyAsync(
            "translategemma:12b",
            2048,
            TestContext.Current.CancellationToken);

        Assert.Equal(2048, proof.ContextTokens);
        Assert.Equal(["translategemma:12b"], transport.Unloaded);
        Assert.Equal(["translategemma:12b:2048"], transport.Preflights);
        Assert.False(runtime.IsDisabled("translategemma:12b", 2048));
    }

    [Fact]
    public async Task Ensure_ready_unloads_other_managed_models_before_preflight()
    {
        var transport = new FakeRuntimeTransport
        {
            Installed = ["translategemma:12b", "qwen2.5-coder:14b"]
        };
        transport.ProcessSnapshots.Enqueue(
        [
            new OllamaProcessInfo(
                "qwen2.5-coder:14b",
                9470098799,
                9470098799,
                4096,
                DateTimeOffset.UtcNow.AddMinutes(30)),
            new OllamaProcessInfo(
                "unmanaged-model:latest",
                100,
                100,
                2048,
                DateTimeOffset.UtcNow.AddMinutes(30))
        ]);
        transport.ProcessSnapshots.Enqueue(
        [
            new OllamaProcessInfo(
                "translategemma:12b",
                8042871520,
                8042871520,
                2048,
                DateTimeOffset.UtcNow.AddMinutes(30))
        ]);
        var runtime = new ModelRuntime(
            transport,
            ModelRoutingCatalog.LoadEmbedded());

        var proof = await runtime.EnsureReadyAsync(
            "translategemma:12b",
            2048,
            TestContext.Current.CancellationToken);

        Assert.True(proof.FullyResident);
        Assert.Equal(["qwen2.5-coder:14b"], transport.Unloaded);
        Assert.DoesNotContain("unmanaged-model:latest", transport.Unloaded);
        Assert.Equal(["translategemma:12b:2048"], transport.Preflights);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(524288)]
    [InlineData(24576)]
    public async Task Ensure_ready_rejects_context_outside_catalog_tiers(int context)
    {
        var runtime = new ModelRuntime(
            new FakeRuntimeTransport(),
            ModelRoutingCatalog.LoadEmbedded());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runtime.EnsureReadyAsync(
                "translategemma:12b",
                context,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pull_rejects_a_model_outside_the_maintenance_allowlist()
    {
        var transport = new FakeRuntimeTransport();
        var runtime = new ModelRuntime(
            transport,
            ModelRoutingCatalog.LoadEmbedded());

        await Assert.ThrowsAsync<ArgumentException>(
            () => runtime.PullAsync(
                "untrusted/model:latest",
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Empty(transport.Pulled);
    }

    // Shared with the residency-policy tests so both suites drive the same fake transport.
    internal sealed class FakeRuntimeTransport : IModelRuntimeTransport
    {
        public IReadOnlyList<string> Installed { get; init; } = [];

        public IReadOnlyList<OllamaProcessInfo> Processes { get; set; } = [];

        public Queue<IReadOnlyList<OllamaProcessInfo>> ProcessSnapshots { get; } = [];

        public List<string> Pulled { get; } = [];

        public List<string> Preflights { get; } = [];

        public List<string> EmbeddingPreflights { get; } = [];

        public List<string> Unloaded { get; } = [];

        public bool RejectEmbeddingGenerationPreflight { get; init; }

        public Task<IReadOnlyList<string>> ListInstalledAsync(CancellationToken ct) =>
            Task.FromResult(Installed);

        public Task<IReadOnlyList<OllamaProcessInfo>> ListProcessesAsync(
            CancellationToken ct) =>
            Task.FromResult(
                ProcessSnapshots.Count > 0
                    ? ProcessSnapshots.Dequeue()
                    : Processes);

        public Task PullAsync(
            string model,
            Func<ModelPullProgress, CancellationToken, Task>? onProgress,
            CancellationToken ct)
        {
            Pulled.Add(model);
            return Task.CompletedTask;
        }

        public Task PreflightAsync(string model, int contextTokens, CancellationToken ct)
        {
            if (RejectEmbeddingGenerationPreflight &&
                string.Equals(
                    model,
                    "qwen3-embedding:8b-q8_0",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Embedding-only models do not support generate preflight.");
            }

            Preflights.Add($"{model}:{contextTokens}");
            return Task.CompletedTask;
        }

        public Task PreflightEmbeddingAsync(
            string model,
            int contextTokens,
            CancellationToken ct)
        {
            EmbeddingPreflights.Add($"{model}:{contextTokens}");
            return Task.CompletedTask;
        }

        public Task UnloadAsync(string model, CancellationToken ct)
        {
            Unloaded.Add(model);
            return Task.CompletedTask;
        }
    }

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
