using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerExecutionRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-execution-router-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Maintenance_pull_requires_current_allowlisted_catalog_entry()
    {
        var fixture = CreateFixture();
        var stale = LocalJobRequestFactory.CreateModelMaintenance(
            "pull",
            LocalJobPriority.Background,
            ModelMaintenanceOperation.Pull,
            "translategemma:12b",
            "stale");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Router.ExecuteAsync(
                stale,
                TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Transport.PulledModels);

        var current = LocalJobRequestFactory.CreateModelMaintenance(
            "pull-current",
            LocalJobPriority.Background,
            ModelMaintenanceOperation.Pull,
            "translategemma:12b",
            fixture.Catalog.CatalogVersion);
        var result = await fixture.Router.ExecuteAsync(
            current,
            TestContext.Current.CancellationToken);

        Assert.Equal(["translategemma:12b"], fixture.Transport.PulledModels);
        Assert.Equal(
            "success",
            result.Body.Deserialize<ModelMaintenanceJobOutput>(
                LocalAiJson.Strict)!.Status);
    }

    [Fact]
    public async Task Routed_chat_selects_live_model_and_returns_routing_receipt()
    {
        var fixture = CreateFixture();
        fixture.Transport.Installed =
            ["translategemma:12b", "qwen3.5:9b"];
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "chat",
            LocalJobPriority.Foreground,
            LocalTaskProfile.PlainTranslation,
            "translate",
            null,
            null,
            new LocalWorkloadMetadata(
                9,
                20,
                1,
                0,
                0,
                LocalDurationClass.Short),
            requestedContextTokens: 2048);

        var result = await fixture.Router.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        var executed = Assert.IsType<ChatJobPayload>(
            Assert.Single(fixture.Executed).Payload);
        Assert.Equal("translategemma:12b", executed.Model);
        Assert.Equal("translategemma:12b", result.Routing!.SelectedModel);
        Assert.Equal("answer", result.Body.Deserialize<ChatJobOutput>()!.Content);
    }

    [Fact]
    public async Task Model_control_status_is_executed_by_control_service()
    {
        var fixture = CreateFixture();
        fixture.Transport.Installed = ["qwen3.5:9b"];
        var request = LocalJobRequestFactory.CreateModelControl(
            "status",
            LocalJobPriority.Interactive,
            ModelControlOperation.Status);

        var result = await fixture.Router.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        var status = result.Body.Deserialize<LocalModelsStatusOutput>(
            LocalAiJson.Strict)!;
        Assert.Equal(["qwen3.5:9b"], status.InstalledModels);
        Assert.Equal(["translategemma:12b"], status.RecommendedMissingModels);
    }

    [Fact]
    public async Task Model_preflight_returns_full_residency_without_chat_content()
    {
        var fixture = CreateFixture();
        fixture.Transport.Installed = ["translategemma:12b"];
        var request = LocalJobRequestFactory.CreateModelPreflight(
            "preflight",
            LocalJobPriority.Interactive,
            "translategemma:12b",
            2048);

        var result = await fixture.Router.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        var output = result.Body.Deserialize<LocalModelPreflightOutput>(
            LocalAiJson.Strict)!;
        Assert.Equal("translategemma:12b", output.Model);
        Assert.Equal(2048, output.ContextTokens);
        Assert.Equal(output.SizeBytes, output.SizeVramBytes);
        Assert.True(output.FullyResident);
        Assert.Equal("translategemma:12b", fixture.Router.ResidentModel);
        Assert.Empty(fixture.Executed);
    }

    [Fact]
    public async Task Idle_unload_releases_and_forgets_the_resident_model()
    {
        var fixture = CreateFixture();
        fixture.Transport.Installed =
            ["translategemma:12b", "qwen3.5:9b"];
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "resident",
            LocalJobPriority.Foreground,
            LocalTaskProfile.PlainTranslation,
            "translate",
            null,
            [],
            new LocalWorkloadMetadata(
                9,
                20,
                1,
                0,
                0,
                LocalDurationClass.Short),
            requestedContextTokens: 2048);
        await fixture.Router.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);
        fixture.Transport.Processes =
        [
            .. fixture.Transport.Processes,
            new OllamaProcessInfo(
                "unmanaged-model:latest",
                100,
                100,
                2048,
                DateTimeOffset.UtcNow.AddMinutes(5))
        ];

        await fixture.Router.UnloadResidentAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["translategemma:12b"], fixture.Transport.UnloadedModels);
        Assert.Contains(
            fixture.Transport.Processes,
            process => process.Model == "unmanaged-model:latest");
        Assert.Null(fixture.Router.ResidentModel);
    }

    [Fact]
    public async Task Prepared_schedule_selection_is_reused_by_execution()
    {
        var fixture = CreateFixture();
        fixture.Transport.Installed =
            ["translategemma:12b", "qwen3.5:9b"];
        var paused = ExperimentSnapshot.Empty;
        for (var attempt = 0; attempt < ExperimentPairState.BatchSize; attempt++)
        {
            paused = paused.Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success);
        }

        await fixture.Experiments.SaveAsync(
            paused,
            TestContext.Current.CancellationToken);
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "prepared-selection",
            LocalJobPriority.Foreground,
            LocalTaskProfile.PlainTranslation,
            "translate",
            null,
            [],
            new LocalWorkloadMetadata(
                9,
                20,
                1,
                0,
                0,
                LocalDurationClass.Short),
            requestedContextTokens: 2048);
        var prepared = await fixture.Router.PrepareAsync(
            [
                new QueuedJobCandidate(
                    request,
                    Sequence: 1,
                    request.CreatedAtUtc)
            ],
            TestContext.Current.CancellationToken);
        await fixture.Experiments.SaveAsync(
            ExperimentSnapshot.Empty,
            TestContext.Current.CancellationToken);

        var result = await fixture.Router.ExecuteAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "qwen3.5:9b",
            Assert.Single(prepared).Value.Model);
        Assert.Equal(
            "qwen3.5:9b",
            Assert.IsType<ChatJobPayload>(
                Assert.Single(fixture.Executed).Payload).Model);
        Assert.Equal("qwen3.5:9b", result.Routing!.SelectedModel);
    }

    [Fact]
    public async Task Invalid_candidate_does_not_block_preparing_valid_work()
    {
        var fixture = CreateFixture();
        fixture.Transport.Installed =
            ["translategemma:12b", "qwen3.5:9b", "qwen2.5-coder:14b"];
        var invalid = LocalJobRequestFactory.ResolveRoutedChat(
            LocalJobRequestFactory.CreateRoutedChat(
                "invalid-override",
                LocalJobPriority.Foreground,
                LocalTaskProfile.PlainTranslation,
                "translate",
                null,
                [],
                new LocalWorkloadMetadata(
                    9,
                    20,
                    1,
                    0,
                    0,
                    LocalDurationClass.Short),
                requestedContextTokens: 2048),
            "qwen2.5-coder:14b");
        var valid = LocalJobRequestFactory.CreateRoutedChat(
            "valid",
            LocalJobPriority.Foreground,
            LocalTaskProfile.PlainTranslation,
            "translate",
            null,
            [],
            new LocalWorkloadMetadata(
                9,
                20,
                1,
                0,
                0,
                LocalDurationClass.Short),
            requestedContextTokens: 2048);

        var prepared = await fixture.Router.PrepareAsync(
            [
                new QueuedJobCandidate(
                    invalid,
                    Sequence: 1,
                    invalid.CreatedAtUtc),
                new QueuedJobCandidate(
                    valid,
                    Sequence: 2,
                    valid.CreatedAtUtc)
            ],
            TestContext.Current.CancellationToken);

        var selection = Assert.Single(prepared);
        Assert.Equal(valid.JobId, selection.Key);
        Assert.Equal("translategemma:12b", selection.Value.Model);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Fixture CreateFixture()
    {
        var catalog = ModelRoutingCatalog.LoadEmbedded();
        var transport = new FakeTransport();
        var runtime = new ModelRuntime(transport, catalog);
        var experiments = new ExperimentStateStore(_root);
        var telemetry = new ModelTelemetryStore(_root);
        var executed = new List<LocalJobRequest>();
        Task<BrokerExecutionResult> Execute(
            LocalJobRequest request,
            CancellationToken cancellationToken)
        {
            executed.Add(request);
            return Task.FromResult(
                new BrokerExecutionResult(
                    JsonSerializer.SerializeToElement(
                        new ChatJobOutput("answer"),
                        LocalAiJson.Strict)));
        }

        var coordinator = new ModelExecutionCoordinator(
            new ModelRouter(catalog),
            runtime,
            experiments,
            telemetry,
            Execute);
        var control = new ModelControlService(
            catalog,
            transport,
            experiments,
            telemetry);
        return new Fixture(
            catalog,
            transport,
            experiments,
            executed,
            new BrokerExecutionRouter(
                catalog,
                transport,
                runtime,
                coordinator,
                control,
                Execute));
    }

    private sealed record Fixture(
        ModelRoutingCatalog Catalog,
        FakeTransport Transport,
        ExperimentStateStore Experiments,
        List<LocalJobRequest> Executed,
        BrokerExecutionRouter Router);

    private sealed class FakeTransport : IModelRuntimeTransport
    {
        public IReadOnlyList<string> Installed { get; set; } = [];
        public IReadOnlyList<OllamaProcessInfo> Processes { get; set; } = [];
        public List<string> PulledModels { get; } = [];
        public List<string> UnloadedModels { get; } = [];

        public Task<IReadOnlyList<string>> ListInstalledAsync(CancellationToken ct) =>
            Task.FromResult(Installed);

        public Task<IReadOnlyList<OllamaProcessInfo>> ListProcessesAsync(CancellationToken ct) =>
            Task.FromResult(Processes);

        public Task PullAsync(string model, CancellationToken ct)
        {
            PulledModels.Add(model);
            return Task.CompletedTask;
        }

        public Task PreflightAsync(
            string model,
            int contextTokens,
            CancellationToken ct)
        {
            Processes =
            [
                new OllamaProcessInfo(
                    model,
                    100,
                    100,
                    contextTokens,
                    DateTimeOffset.UtcNow.AddMinutes(5))
            ];
            return Task.CompletedTask;
        }

        public Task PreflightEmbeddingAsync(
            string model,
            int contextTokens,
            CancellationToken ct) =>
            PreflightAsync(model, contextTokens, ct);

        public Task UnloadAsync(string model, CancellationToken ct)
        {
            UnloadedModels.Add(model);
            Processes = Processes
                .Where(process => !string.Equals(
                    process.Model,
                    model,
                    StringComparison.Ordinal))
                .ToArray();
            return Task.CompletedTask;
        }
    }
}
