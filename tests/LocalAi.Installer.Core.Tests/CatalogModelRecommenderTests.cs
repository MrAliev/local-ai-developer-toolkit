using LocalAi.Contracts;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.Core.Tests;

public sealed class CatalogModelRecommenderTests
{
    private const long TwoGb = 2L * 1024 * 1024 * 1024;

    private static ModelCatalogEntry Entry(string tag, params int[] contexts) =>
        new(
            tag,
            "ollama",
            LocalModelLifecycle.Established,
            LocalModelInstallPolicy.Existing,
            [LocalModelCapability.Text],
            contexts,
            false,
            null);

    private static GpuSnapshot Gpu(ulong dedicatedBytes) =>
        new(
            ObservationState.Available,
            [new GpuAdapterSnapshot("adapter-1", "Test Adapter", dedicatedBytes, false)],
            null);

    private sealed class FixedSizes(long? size) : IModelSizeSource
    {
        public List<string> Requested { get; } = [];

        public Task<long?> GetDownloadSizeBytesAsync(
            string tag,
            CancellationToken cancellationToken)
        {
            Requested.Add(tag);
            return Task.FromResult(size);
        }
    }

    [Fact]
    public async Task Without_sizes_no_recommendation_is_invented()
    {
        var sizes = new FixedSizes(null);
        var recommender = new CatalogModelRecommender(sizes);

        var result = await recommender.RecommendAsync(
            Gpu(16UL * 1024 * 1024 * 1024),
            [Entry("test:1", 2048)],
            TestContext.Current.CancellationToken);

        Assert.False(result.SizesKnown);
        Assert.Empty(result.Fits);
        Assert.Contains("could not be retrieved", result.AdapterExplanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_fits_the_adapter_is_enabled()
    {
        var recommender = new CatalogModelRecommender(new FixedSizes(TwoGb));

        var result = await recommender.RecommendAsync(
            Gpu(24UL * 1024 * 1024 * 1024),
            [Entry("test:1", 2048)],
            TestContext.Current.CancellationToken);

        Assert.True(result.SizesKnown);
        var fit = Assert.Single(result.Fits);
        Assert.True(fit.FitsInVideoMemory);
        Assert.Equal(TwoGb, fit.DownloadSizeBytes);
    }

    [Fact]
    public async Task A_model_larger_than_the_adapter_is_reported_as_not_fitting()
    {
        var recommender = new CatalogModelRecommender(new FixedSizes(TwoGb));

        var result = await recommender.RecommendAsync(
            Gpu(1UL * 1024 * 1024 * 1024),
            [Entry("test:1", 2048)],
            TestContext.Current.CancellationToken);

        var fit = Assert.Single(result.Fits);
        Assert.False(fit.FitsInVideoMemory);
        Assert.Empty(result.Fitting);
    }

    [Fact]
    public async Task A_machine_without_dedicated_memory_fits_nothing()
    {
        // This is the integrated-graphics case: the strict policy has nothing to offer, and
        // the wizard is expected to point at the residency setting instead of pretending.
        var recommender = new CatalogModelRecommender(new FixedSizes(TwoGb));

        var result = await recommender.RecommendAsync(
            Gpu(0),
            [Entry("test:1", 2048)],
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Fitting);
    }

    [Fact]
    public async Task Every_permitted_context_is_weighed_separately()
    {
        var recommender = new CatalogModelRecommender(new FixedSizes(TwoGb));

        var result = await recommender.RecommendAsync(
            Gpu(6UL * 1024 * 1024 * 1024),
            [Entry("test:1", 2048, 32768)],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Fits.Count);
        // A larger context needs more memory, so the small one must fit at least as often.
        var small = result.Fits.Single(fit => fit.ContextTokens == 2048);
        var large = result.Fits.Single(fit => fit.ContextTokens == 32768);
        Assert.True(small.FitsInVideoMemory);
        Assert.False(large.FitsInVideoMemory);
    }

    [Fact]
    public async Task Each_catalogue_tag_is_asked_for_exactly_once()
    {
        var sizes = new FixedSizes(TwoGb);
        var recommender = new CatalogModelRecommender(sizes);

        await recommender.RecommendAsync(
            Gpu(24UL * 1024 * 1024 * 1024),
            [Entry("a:1", 2048, 4096), Entry("b:1", 2048)],
            TestContext.Current.CancellationToken);

        Assert.Equal(["a:1", "b:1"], sizes.Requested);
    }
}
