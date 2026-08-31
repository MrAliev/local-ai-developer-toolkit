using System.Globalization;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Tests;

public sealed class ModelRecommendationEngineTests
{
    private static readonly ModelMemoryReservePolicy TestPolicy = new(100, 2);

    [Theory]
    [InlineData(ObservationState.Unavailable)]
    [InlineData(ObservationState.Failed)]
    [InlineData(ObservationState.Unknown)]
    public void Non_available_gpu_observation_disables_every_choice(ObservationState state)
    {
        var result = Recommend(
            new GpuSnapshot(state, [Gpu("gpu", 10_000)], "probe result"),
            [Model("small", 2048, 1_000)]);

        Assert.Equal(AdapterSelectionStatus.NoEligibleAdapter, result.AdapterSelectionStatus);
        Assert.Null(result.SelectedAdapter);
        Assert.False(Assert.Single(result.Choices).IsEnabled);
        Assert.Contains("no eligible dedicated GPU", result.Choices[0].Explanation);
        Assert.Null(result.Minimal);
        Assert.Null(result.Recommended);
        Assert.Null(result.Extended);
    }

    [Fact]
    public void Available_observation_without_adapters_has_no_gpu_recommendations()
    {
        var result = Recommend(
            new GpuSnapshot(ObservationState.Available, [], null),
            [Model("small", 2048, 1)]);

        Assert.Equal(AdapterSelectionStatus.NoEligibleAdapter, result.AdapterSelectionStatus);
        Assert.Null(result.SelectedAdapter);
        Assert.False(Assert.Single(result.Choices).IsEnabled);
        Assert.Null(result.Recommended);
    }

    [Fact]
    public void One_gpu_uses_only_dedicated_memory_and_reports_all_estimate_components()
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu-a", 5_000)),
            [Model("model-a", 2048, 800)]);

        var choice = Assert.Single(result.Choices);
        Assert.Equal("gpu-a", result.SelectedAdapter!.StableId);
        Assert.Equal(1UL, choice.SignedDownloadSizeBytes);
        Assert.Equal(800UL, choice.SignedBaseEstimateBytes);
        Assert.Equal(100UL, choice.RuntimeReserveBytes);
        Assert.Equal(4096UL, choice.ContextReserveBytes);
        Assert.Equal(4996UL, choice.RequiredBytes);
        Assert.Equal(5000UL, choice.AvailableDedicatedBytes);
        Assert.Equal(4UL, choice.HeadroomBytes);
        Assert.True(choice.IsEnabled);
        Assert.Contains("estimate", choice.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broker preflight", choice.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not proof", result.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_adapter_is_largest_with_ordinal_stable_id_tie_break()
    {
        var result = Recommend(
            AvailableGpu(
                Gpu("z", 9_000),
                Gpu("b", 10_000),
                Gpu("a", 10_000)),
            [Model("small", 2048, 1)]);

        Assert.Equal("a", result.SelectedAdapter!.StableId);
        Assert.Equal(10_000UL, result.Choices[0].AvailableDedicatedBytes);
    }

    [Fact]
    public void Exact_manual_adapter_is_used_without_aggregating_other_adapters()
    {
        var result = Recommend(
            AvailableGpu(Gpu("small", 5_000), Gpu("large", 8_000)),
            [Model("model", 2048, 1_000)],
            manualAdapterStableId: "small");

        Assert.Equal("small", result.SelectedAdapter!.StableId);
        Assert.Equal(5_000UL, result.Choices[0].AvailableDedicatedBytes);
        Assert.False(result.Choices[0].IsEnabled);
    }

    [Fact]
    public void Invalid_manual_adapter_never_falls_back()
    {
        var result = Recommend(
            AvailableGpu(Gpu("eligible", 10_000)),
            [Model("small", 2048, 1)],
            manualAdapterStableId: "missing");

        Assert.Equal(AdapterSelectionStatus.InvalidManualSelection, result.AdapterSelectionStatus);
        Assert.Null(result.SelectedAdapter);
        Assert.Contains("missing", result.AdapterSelectionExplanation);
        Assert.False(result.Choices[0].IsEnabled);
    }

    [Fact]
    public void Software_and_zero_dedicated_shared_memory_adapters_are_excluded()
    {
        var result = Recommend(
            AvailableGpu(
                new GpuAdapterSnapshot("software", "Software", ulong.MaxValue, true),
                Gpu("shared-only", 0),
                Gpu("discrete", 5_000)),
            [Model("model", 2048, 1_000)]);

        Assert.Equal("discrete", result.SelectedAdapter!.StableId);
        Assert.Equal(5_000UL, result.Choices[0].AvailableDedicatedBytes);
        Assert.False(result.Choices[0].IsEnabled);
    }

    [Fact]
    public void Exact_required_memory_boundary_is_enabled_and_one_byte_over_is_disabled()
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 5_000)),
            [Model("exact", 2048, 804), Model("over", 2048, 805)]);

        Assert.True(result.Choices.Single(choice => choice.Name == "exact").IsEnabled);
        var over = result.Choices.Single(choice => choice.Name == "over");
        Assert.False(over.IsEnabled);
        Assert.Equal(1UL, over.OverBudgetBytes);
        Assert.Contains("exceed", over.Explanation);
    }

    [Fact]
    public void Runtime_and_context_policy_values_are_independently_injectable()
    {
        var policy = new ModelMemoryReservePolicy(777, 3);
        var result = new ModelRecommendationEngine(policy).Recommend(
            AvailableGpu(Gpu("gpu", 10_000)),
            [Model("model", 4096, 1_000)]);

        var choice = Assert.Single(result.Choices);
        Assert.Equal(777UL, choice.RuntimeReserveBytes);
        Assert.Equal(12_288UL, choice.ContextReserveBytes);
        Assert.Equal(14_065UL, choice.RequiredBytes);
    }

    [Fact]
    public void Overflow_saturates_and_disables_choice_instead_of_wrapping()
    {
        var engine = new ModelRecommendationEngine(
            new ModelMemoryReservePolicy(1, ulong.MaxValue));

        var choice = Assert.Single(engine.Recommend(
            AvailableGpu(Gpu("gpu", ulong.MaxValue)),
            [Model("model", 2048, 1)]).Choices);

        Assert.False(choice.IsEnabled);
        Assert.Equal(ulong.MaxValue, choice.ContextReserveBytes);
        Assert.Equal(ulong.MaxValue, choice.RequiredBytes);
        Assert.Contains("overflow", choice.Explanation);
    }

    [Fact]
    public void Tiers_use_smallest_lower_median_and_largest_enabled_options()
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 100_000)),
            [
                Model("large", 2048, 4_000),
                Model("small", 2048, 1_000),
                Model("upper-middle", 2048, 3_000),
                Model("lower-middle", 2048, 2_000),
            ]);

        Assert.Equal("small", result.Minimal!.Name);
        Assert.Equal("lower-middle", result.Recommended!.Name);
        Assert.Equal("large", result.Extended!.Name);
        Assert.Equal(["small", "lower-middle", "upper-middle", "large"],
            result.Choices.Select(choice => choice.Name));
    }

    [Fact]
    public void Tier_semantics_are_stable_for_zero_one_and_two_enabled_options()
    {
        var none = Recommend(AvailableGpu(Gpu("gpu", 1)), [Model("none", 2048, 1)]);
        Assert.Null(none.Minimal);
        Assert.Null(none.Recommended);
        Assert.Null(none.Extended);

        var one = Recommend(AvailableGpu(Gpu("gpu", 10_000)), [Model("one", 2048, 1)]);
        Assert.Same(one.Minimal, one.Recommended);
        Assert.Same(one.Recommended, one.Extended);

        var two = Recommend(
            AvailableGpu(Gpu("gpu", 10_000)),
            [Model("two", 2048, 2), Model("one", 2048, 1)]);
        Assert.Equal("one", two.Minimal!.Name);
        Assert.Same(two.Minimal, two.Recommended);
        Assert.Equal("two", two.Extended!.Name);
    }

    [Fact]
    public void Stable_order_uses_required_bytes_then_ordinal_name_then_context()
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 100_000)),
            [
                Model("z", 2048, 2_049),
                Model("a", 4096, 2_049),
                Model("a", 2048, 2_049),
            ],
            new ModelMemoryReservePolicy(1, 0));

        Assert.All(result.Choices, choice => Assert.True(choice.IsEnabled));
        Assert.Equal(
            [("a", 2048), ("a", 4096), ("z", 2048)],
            result.Choices.Select(choice => (choice.Name, choice.ContextTokens)));
    }

    [Fact]
    public void Manual_model_selection_preserves_enabled_and_disabled_choices()
    {
        var catalog = new[] { Model("fit", 2048, 1), Model("large", 4096, 20_000) };
        var enabled = Recommend(
            AvailableGpu(Gpu("gpu", 10_000)), catalog,
            manualModel: new ModelSelection("fit", 2048));
        var disabled = Recommend(
            AvailableGpu(Gpu("gpu", 10_000)), catalog,
            manualModel: new ModelSelection("large", 4096));

        Assert.Equal(ManualModelSelectionStatus.Selected, enabled.ManualSelectionStatus);
        Assert.True(enabled.ManualChoice!.IsEnabled);
        Assert.Equal(ManualModelSelectionStatus.Selected, disabled.ManualSelectionStatus);
        Assert.False(disabled.ManualChoice!.IsEnabled);
        Assert.Contains("exceed", disabled.ManualChoice.Explanation);
    }

    [Fact]
    public void Manual_model_selection_uses_exact_name_and_context_identity()
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 100_000)),
            [
                Model("same-model", 2048, 1_000),
                Model("same-model", 4096, 1_000),
            ],
            manualModel: new ModelSelection("same-model", 4096));

        Assert.Equal(ManualModelSelectionStatus.Selected, result.ManualSelectionStatus);
        Assert.NotNull(result.ManualChoice);
        Assert.Equal("same-model", result.ManualChoice.Name);
        Assert.Equal(4096, result.ManualChoice.ContextTokens);
        Assert.Equal(1_000UL, result.ManualChoice.SignedBaseEstimateBytes);
        Assert.Equal(9_292UL, result.ManualChoice.RequiredBytes);
    }

    [Fact]
    public void Unknown_manual_model_is_an_explicit_invalid_result()
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 10_000)),
            [Model("known", 2048, 1)],
            manualModel: new ModelSelection("unknown", 2048));

        Assert.Equal(ManualModelSelectionStatus.Unknown, result.ManualSelectionStatus);
        Assert.Null(result.ManualChoice);
        Assert.Contains("unknown", result.ManualSelectionExplanation);
    }

    [Fact]
    public void Duplicate_semantic_model_options_are_disabled_and_manual_selection_is_ambiguous()
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 10_000)),
            [Model("model", 2048, 1), Model("model", 2048, 1)],
            manualModel: new ModelSelection("model", 2048));

        Assert.All(result.Choices, choice =>
        {
            Assert.False(choice.IsEnabled);
            Assert.Contains("duplicate", choice.Explanation);
        });
        Assert.Equal(ManualModelSelectionStatus.Ambiguous, result.ManualSelectionStatus);
        Assert.Null(result.ManualChoice);
    }

    [Theory]
    [InlineData("MODEL", 1, 1_000, "casing")]
    [InlineData("model", 2, 1_000, "download size")]
    [InlineData("model", 1, 2_000, "base VRAM")]
    public void Inconsistent_model_family_metadata_disables_every_context_variant(
        string secondName,
        long secondDownloadSize,
        long secondBaseVramBytes,
        string expectedReason)
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 100_000)),
            [
                new ManifestModel("model", 2048, 1, 1_000),
                new ManifestModel(secondName, 4096, secondDownloadSize, secondBaseVramBytes),
            ]);

        Assert.All(result.Choices, choice =>
        {
            Assert.False(choice.IsEnabled);
            Assert.Contains(expectedReason, choice.Explanation);
        });
        Assert.Null(result.Minimal);
        Assert.Null(result.Recommended);
        Assert.Null(result.Extended);
    }

    [Fact]
    public void Recommendation_explanations_are_identical_across_cultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var russian = CaptureExplanations(TestCulture("ru-RU", "minus-ru"));
            var arabic = CaptureExplanations(TestCulture("ar-SA", "minus-ar"));

            Assert.Equal(russian, arabic);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unsafe model")]
    public void Invalid_model_names_appear_as_disabled_manual_choices(string name)
    {
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 10_000)),
            [Model(name, 2048, 1)],
            manualModel: new ModelSelection(name, 2048));

        Assert.False(Assert.Single(result.Choices).IsEnabled);
        Assert.Contains("invalid model name", result.Choices[0].Explanation);
        Assert.Equal(ManualModelSelectionStatus.Selected, result.ManualSelectionStatus);
        Assert.Same(result.Choices[0], result.ManualChoice);
    }

    [Theory]
    [InlineData(1024, 1)]
    [InlineData(3072, 1)]
    [InlineData(2048, 0)]
    [InlineData(2048, -1)]
    public void Invalid_context_or_estimate_is_a_disabled_choice(int contextTokens, long estimate)
    {
        var choice = Assert.Single(Recommend(
            AvailableGpu(Gpu("gpu", 10_000)),
            [Model("model", contextTokens, estimate)]).Choices);

        Assert.False(choice.IsEnabled);
        Assert.Contains("invalid", choice.Explanation);
    }

    [Fact]
    public void Recommendation_is_a_deep_immutable_snapshot_of_inputs()
    {
        var adapters = new List<GpuAdapterSnapshot> { Gpu("gpu", 10_000) };
        var catalog = new List<ManifestModel> { Model("model", 2048, 1) };
        var result = Recommend(new GpuSnapshot(ObservationState.Available, adapters, null), catalog);

        adapters[0] = Gpu("changed", 1);
        catalog[0] = Model("changed", 2048, 20_000);
        adapters.Add(Gpu("later", ulong.MaxValue));
        catalog.Add(Model("later", 2048, 1));

        Assert.Equal("gpu", result.SelectedAdapter!.StableId);
        Assert.Equal("model", Assert.Single(result.Choices).Name);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ModelRecommendationChoice>)result.Choices).Add(result.Choices[0]));
    }

    [Fact]
    public void Null_catalog_or_entry_and_duplicate_adapter_ids_are_rejected()
    {
        var engine = new ModelRecommendationEngine(TestPolicy);
        Assert.Throws<ArgumentNullException>(() =>
            engine.Recommend(null!, [Model("model", 2048, 1)]));
        Assert.Throws<ArgumentNullException>(() =>
            engine.Recommend(AvailableGpu(Gpu("gpu", 1)), null!));
        Assert.Throws<ArgumentException>(() =>
            engine.Recommend(AvailableGpu(Gpu("gpu", 1)), [null!]));
        Assert.Throws<ArgumentException>(() =>
            engine.Recommend(
                AvailableGpu(Gpu("gpu", 1), Gpu("gpu", 2)),
                [Model("model", 2048, 1)]));
    }

    private static ModelRecommendation Recommend(
        GpuSnapshot gpu,
        IEnumerable<ManifestModel> catalog,
        ModelMemoryReservePolicy? policy = null,
        string? manualAdapterStableId = null,
        ModelSelection? manualModel = null) =>
        new ModelRecommendationEngine(policy ?? TestPolicy).Recommend(
            gpu, catalog, manualAdapterStableId, manualModel);

    private static string[] CaptureExplanations(CultureInfo culture)
    {
        CultureInfo.CurrentCulture = culture;
        var result = Recommend(
            AvailableGpu(Gpu("gpu", 5_000)),
            [Model("model", 2048, 805)],
            manualModel: new ModelSelection("unknown", 4096));
        return
        [
            result.AdapterSelectionExplanation,
            Assert.Single(result.Choices).Explanation,
            result.ManualSelectionExplanation,
        ];
    }

    private static CultureInfo TestCulture(string name, string negativeSign)
    {
        try
        {
            return new CultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            var fallback = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            fallback.NumberFormat.NegativeSign = negativeSign;
            return fallback;
        }
    }

    private static GpuSnapshot AvailableGpu(params GpuAdapterSnapshot[] adapters) =>
        new(ObservationState.Available, adapters, null);

    private static GpuAdapterSnapshot Gpu(string id, ulong dedicatedBytes) =>
        new(id, id, dedicatedBytes, false);

    private static ManifestModel Model(string name, int contextTokens, long estimate) =>
        new(name, contextTokens, 1, estimate);
}
