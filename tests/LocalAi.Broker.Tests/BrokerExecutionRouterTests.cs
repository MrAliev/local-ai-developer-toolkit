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
                progress: null,
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
            progress: null,
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
            progress: null,
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
            progress: null,
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
            2048,
            fixture.Catalog.CatalogVersion);

        var result = await fixture.Router.ExecuteAsync(
            request,
            progress: null,
            TestContext.Current.CancellationToken);

        var output = result.Body.Deserialize<LocalModelPreflightOutput>(
            LocalAiJson.Strict)!;
        Assert.Equal("translategemma:12b", output.Model);
        Assert.Equal(2048, output.ContextTokens);
        Assert.Equal(fixture.Catalog.CatalogVersion, output.CatalogVersion);
        Assert.Equal(output.SizeBytes, output.SizeVramBytes);
        Assert.True(output.FullyResident);
        Assert.Equal("translategemma:12b", fixture.Router.ResidentModel);
        Assert.Empty(fixture.Executed);
    }

    [Fact]
    public async Task Model_preflight_rejects_stale_catalog_before_runtime_call()
    {
        var fixture = CreateFixture();
        fixture.Transport.Installed = ["translategemma:12b"];
        var request = LocalJobRequestFactory.CreateModelPreflight(
            "preflight-stale",
            LocalJobPriority.Interactive,
            "translategemma:12b",
            2048,
            "stale");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Router.ExecuteAsync(
                request,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Transport.Processes);
        Assert.Null(fixture.Router.ResidentModel);
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
            progress: null,
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
    public async Task Direct_embedding_is_tracked_for_idle_unload()
    {
        var fixture = CreateFixture();
        var request = LocalJobRequestFactory.CreateEmbed(
            "embedding",
            LocalJobPriority.Foreground,
            "qwen3-embedding:8b-q8_0",
            ["semantic navigation"]);

        await fixture.Router.ExecuteAsync(
            request,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("qwen3-embedding:8b-q8_0", fixture.Router.ResidentModel);

        await fixture.Router.UnloadResidentAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["qwen3-embedding:8b-q8_0"], fixture.Transport.UnloadedModels);
        Assert.Null(fixture.Router.ResidentModel);
    }

    /// <summary>
    /// The scheduler asks which model is loaded to decide whether the next job is a cold switch,
    /// and a model outside the catalog was never warm by that measure — so every job targeting
    /// one paid the two-second gather window, back to back, for a whole indexing run.
    /// </summary>
    [Fact]
    public async Task A_model_the_catalog_does_not_name_is_still_what_is_loaded()
    {
        var fixture = CreateFixture();
        var request = LocalJobRequestFactory.CreateEmbed(
            "embedding",
            LocalJobPriority.Foreground,
            "some-other-embedding:4b",
            ["semantic navigation"]);

        await fixture.Router.ExecuteAsync(
            request,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("some-other-embedding:4b", fixture.Router.ResidentModel);
    }

    /// <summary>
    /// And remembering it changes nothing about what this broker is willing to unload: a model
    /// the catalog does not name was loaded by somebody else, and taking it out from under them
    /// is the thing the filter in UnloadResidentAsync exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_model_the_catalog_does_not_name_is_never_unloaded()
    {
        var fixture = CreateFixture();
        await fixture.Router.ExecuteAsync(
            LocalJobRequestFactory.CreateEmbed(
                "embedding",
                LocalJobPriority.Foreground,
                "some-other-embedding:4b",
                ["semantic navigation"]),
            progress: null,
            TestContext.Current.CancellationToken);

        await fixture.Router.UnloadResidentAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Transport.UnloadedModels);
        Assert.Null(fixture.Router.ResidentModel);
    }

    [Fact]
    public async Task Direct_native_generate_is_tracked_for_idle_unload()
    {
        var fixture = CreateFixture();
        using var body = JsonDocument.Parse(
            """{"model":"gpt-oss:20b","prompt":"smoke","stream":false}""");
        var request = LocalJobRequestFactory.CreateNativeOllama(
            "native-generate",
            LocalJobPriority.Foreground,
            NativeOllamaOperation.Generate,
            body.RootElement.Clone());

        await fixture.Router.ExecuteAsync(
            request,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("gpt-oss:20b", fixture.Router.ResidentModel);

        await fixture.Router.UnloadResidentAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["gpt-oss:20b"], fixture.Transport.UnloadedModels);
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
            progress: null,
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

    /// <summary>
    /// One candidate the resolution cannot read must cost that job and no other.
    ///
    /// This runs over the whole queue at once, so an exception here reached the host, which read
    /// it as "nothing to schedule" and leased nothing — at any priority, for as long as the job
    /// stayed queued. #335 recorded the result: five jobs waiting with AttemptCount 0, a git
    /// commit hanging ten minutes behind a job nothing had even attempted, and a broker calling
    /// itself healthy throughout.
    ///
    /// The exception is a KeyNotFoundException: the catalog throws it for a tag it does not hold,
    /// and a model override names one whenever somebody types `--model no-such-model:1b`. The
    /// guard here caught InvalidOperationException only, so that one walked out.
    /// </summary>
    [Fact]
    public async Task A_candidate_naming_a_model_the_catalog_never_heard_of_takes_no_others_with_it()
    {
        var fixture = CreateFixture();
        // The good candidate has to be routable, or it would be missing for a reason that
        // has nothing to do with the one being tested.
        fixture.Transport.Installed = fixture.Catalog.Models
            .Select(model => model.Tag)
            .ToArray();
        var workload = new LocalWorkloadMetadata(2, 2, 0, 0, 0, LocalDurationClass.Short);
        var unreadable = LocalJobRequestFactory.Create(
            "unreadable",
            LocalJobPriority.Interactive,
            new ChatJobPayload(
                "no-such-model:1b",
                "hi",
                null,
                null,
                LocalTaskProfile.ShortSummary,
                workload));
        var wanted = LocalJobRequestFactory.CreateRoutedChat(
            "wanted",
            LocalJobPriority.Interactive,
            LocalTaskProfile.ShortSummary,
            "hi",
            null,
            null,
            workload);

        var prepared = await fixture.Router.PrepareAsync(
            [
                new QueuedJobCandidate(unreadable, 1, DateTimeOffset.UtcNow),
                new QueuedJobCandidate(wanted, 2, DateTimeOffset.UtcNow),
            ],
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(unreadable.JobId, prepared.Keys);
        Assert.Contains(wanted.JobId, prepared.Keys);
    }

    /// <summary>
    /// Skipped by the scheduler is not the same as answered. A job the catalog cannot route still
    /// has to end, and it ends where every other unservable job ends: leased, attempted once, and
    /// failed for itself — with a reason that names what could not be found, which the queue now
    /// keeps beside the job (#354).
    /// </summary>
    [Fact]
    public async Task A_job_naming_a_model_that_does_not_exist_fails_rather_than_waits()
    {
        var fixture = CreateFixture();
        fixture.Transport.Installed = fixture.Catalog.Models
            .Select(model => model.Tag)
            .ToArray();
        var request = LocalJobRequestFactory.Create(
            "unreadable",
            LocalJobPriority.Interactive,
            new ChatJobPayload(
                "no-such-model:1b",
                "hi",
                null,
                null,
                LocalTaskProfile.ShortSummary,
                new LocalWorkloadMetadata(2, 2, 0, 0, 0, LocalDurationClass.Short)));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Router.ExecuteAsync(
                request,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Contains("no-such-model:1b", failure.Message, StringComparison.Ordinal);
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

        public Task PullAsync(
            string model,
            Func<ModelPullProgress, CancellationToken, Task>? onProgress,
            CancellationToken ct)
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
