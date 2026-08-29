using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelRouterTests
{
    private static readonly ModelRoutingCatalog Catalog =
        ModelRoutingCatalog.LoadEmbedded();

    [Fact]
    public void Experimental_candidate_runs_for_attempts_one_through_ten_then_pauses()
    {
        var router = new ModelRouter(Catalog);
        var availability = Availability(
            installed: ["translategemma:12b", "qwen3.5:9b"]);
        var state = ExperimentSnapshot.Empty;

        for (var completed = 0; completed < 10; completed++)
        {
            var selection = router.Select(
                Request(LocalTaskProfile.PlainTranslation),
                availability,
                state);

            Assert.Equal("translategemma:12b", selection!.Model);
            Assert.True(selection.IsExperimentalAttempt);
            state = state.Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success);
        }

        var fallback = router.Select(
            Request(LocalTaskProfile.PlainTranslation),
            availability,
            state);

        Assert.Equal("qwen3.5:9b", fallback!.Model);
        Assert.False(fallback.IsExperimentalAttempt);
        Assert.True(
            state.Pair(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b").IsPaused);
    }

    [Fact]
    public void Experiment_counters_are_independent_per_task_profile()
    {
        var router = new ModelRouter(Catalog);
        var availability = Availability(
            installed:
            [
                "translategemma:12b",
                "qwen3.5:9b",
                "qwen2.5-coder:14b"
            ]);
        var state = ExperimentSnapshot.Empty;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            state = state.Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success);
        }

        var selection = router.Select(
            Request(LocalTaskProfile.TechnicalTranslation),
            availability,
            state);

        Assert.Equal("translategemma:12b", selection!.Model);
        Assert.True(selection.IsExperimentalAttempt);
        Assert.Equal(
            0,
            state.Pair(
                LocalTaskProfile.TechnicalTranslation,
                "translategemma:12b").CompletedAttempts);
    }

    [Fact]
    public void Explicit_override_requires_installation_route_capability_and_context()
    {
        var router = new ModelRouter(Catalog);

        Assert.Throws<InvalidOperationException>(
            () => router.Select(
                Request(
                    LocalTaskProfile.PlainTranslation,
                    modelOverride: "translategemma:12b"),
                Availability(installed: ["qwen3.5:9b"]),
                ExperimentSnapshot.Empty));
        Assert.Throws<InvalidOperationException>(
            () => router.Select(
                Request(
                    LocalTaskProfile.PlainTranslation,
                    modelOverride: "qwen3-embedding:8b-q8_0"),
                Availability(installed: ["qwen3-embedding:8b-q8_0"]),
                ExperimentSnapshot.Empty));
        Assert.Throws<InvalidOperationException>(
            () => router.Select(
                Request(
                    LocalTaskProfile.PlainTranslation,
                    contextTokens: 262144,
                    modelOverride: "translategemma:12b"),
                Availability(installed: ["translategemma:12b"]),
                ExperimentSnapshot.Empty));
    }

    /// <summary>
    /// The failure code the broker records is this exception's type name, and it is the only
    /// thing that reaches the client -- messages do not cross that boundary. read_image reads the
    /// code to say which model to install, so the type is a contract between the two, not an
    /// implementation detail.
    /// </summary>
    [Fact]
    public void No_installed_model_for_a_profile_fails_by_a_name_the_client_can_act_on()
    {
        var router = new ModelRouter(Catalog);

        var exception = Assert.Throws<NoModelInstalledException>(
            () => router.Select(
                Request(LocalTaskProfile.VisualAnalysis),
                Availability(installed: ["qwen2.5-coder:14b", "translategemma:12b"]),
                ExperimentSnapshot.Empty));

        Assert.Equal(LocalTaskProfile.VisualAnalysis, exception.Profile);
        Assert.Equal("NoModelInstalledException", exception.GetType().Name);
    }

    [Fact]
    public void Image_workload_rejects_a_model_below_the_required_pixel_capacity()
    {
        var router = new ModelRouter(Catalog);
        var oversized = new LocalWorkloadMetadata(
            10,
            100,
            0,
            1,
            5_000_000,
            LocalDurationClass.Medium);

        // Installed but unusable for this request, which is the opposite instruction to "install
        // one": the two are separate types so the client can tell them apart from the code alone.
        Assert.Throws<NoEligibleModelException>(
            () => router.Select(
                Request(
                    LocalTaskProfile.VisualAnalysis,
                    workload: oversized),
                Availability(installed: ["qwen3.5:9b"]),
                ExperimentSnapshot.Empty));

        var supported = router.Select(
            Request(
                LocalTaskProfile.VisualAnalysis,
                workload: new LocalWorkloadMetadata(
                    10,
                    100,
                    0,
                    1,
                    1_000_000,
                    LocalDurationClass.Medium)),
            Availability(installed: ["qwen3.5:9b"]),
            ExperimentSnapshot.Empty);
        Assert.Equal("qwen3.5:9b", supported!.Model);
    }

    [Theory]
    [InlineData(ModelExecutionOutcome.StructuralFailure)]
    [InlineData(ModelExecutionOutcome.ContextFailure)]
    [InlineData(ModelExecutionOutcome.TechnicalFailure)]
    public void Failed_experimental_attempt_selects_established_fallback(
        ModelExecutionOutcome outcome)
    {
        var router = new ModelRouter(Catalog);
        var availability = Availability(
            installed: ["translategemma:12b", "qwen3.5:9b"]);
        var selection = router.Select(
            Request(LocalTaskProfile.PlainTranslation),
            availability,
            ExperimentSnapshot.Empty)!;

        var fallback = router.SelectFallback(selection, outcome, availability);

        Assert.Equal("qwen3.5:9b", fallback.Model);
        Assert.True(fallback.UsedFallback);
    }

    [Fact]
    public void Two_consecutive_technical_failures_open_circuit_and_success_resets_it()
    {
        var state = ExperimentSnapshot.Empty
            .Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.TechnicalFailure)
            .Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.Success)
            .Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.TechnicalFailure)
            .Record(
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ModelExecutionOutcome.TechnicalFailure);

        var pair = state.Pair(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b");
        Assert.Equal(2, pair.ConsecutiveTechnicalFailures);
        Assert.True(pair.IsCircuitOpen);

        var selection = new ModelRouter(Catalog).Select(
            Request(LocalTaskProfile.PlainTranslation),
            Availability(installed: ["translategemma:12b", "qwen3.5:9b"]),
            state);
        Assert.Equal("qwen3.5:9b", selection!.Model);
    }

    [Fact]
    public void Promotion_prefers_a_suitable_resident_model()
    {
        var router = new ModelRouter(Catalog);
        var state = ExperimentSnapshot.Empty.ApplyFeedback(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ExperimentOwnerAction.Promote);

        var selection = router.Select(
            Request(LocalTaskProfile.PlainTranslation),
            Availability(
                installed: ["translategemma:12b", "qwen3.5:9b"],
                resident: ["translategemma:12b"]),
            state);

        Assert.Equal("translategemma:12b", selection!.Model);
        Assert.False(selection.IsExperimentalAttempt);
    }

    [Fact]
    public void Exact_search_never_selects_a_language_model()
    {
        var selection = new ModelRouter(Catalog).Select(
            Request(LocalTaskProfile.ExactSearch),
            Availability(
                installed:
                [
                    "qwen3.5:9b",
                    "qwen2.5-coder:14b",
                    "gpt-oss:20b"
                ]),
            ExperimentSnapshot.Empty);

        Assert.Null(selection);
    }

    private static ModelRoutingRequest Request(
        LocalTaskProfile profile,
        int contextTokens = 2048,
        string? modelOverride = null,
        LocalWorkloadMetadata? workload = null) =>
        new(profile, contextTokens, modelOverride, workload);

    private static ModelAvailability Availability(
        IReadOnlyList<string> installed,
        IReadOnlyList<string>? resident = null) =>
        new(installed, resident ?? [], []);
}
