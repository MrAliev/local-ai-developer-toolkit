using LocalAi.Contracts;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Models;

public sealed record CatalogModelFit(
    string Tag,
    int ContextTokens,
    bool FitsInVideoMemory,
    long DownloadSizeBytes,
    string Explanation)
{
    public bool HasKnownSize => DownloadSizeBytes > 0;
}

public sealed record CatalogRecommendation(
    IReadOnlyList<CatalogModelFit> Fits,
    string AdapterExplanation,
    bool SizesKnown)
{
    public static CatalogRecommendation Empty { get; } =
        new([], "No graphics adapter information was available.", false);

    public IReadOnlyList<CatalogModelFit> Fitting =>
        [.. Fits.Where(fit => fit.FitsInVideoMemory)];
}

/// <summary>
/// Turns the routing catalogue into a hardware-aware recommendation.
///
/// The catalogue supplies what a model is for and which context sizes it permits; the
/// registry supplies how big it is; the diagnosis supplies what the machine has. Only all
/// three together answer "will this run here", which is why none of them is hardcoded.
/// </summary>
public sealed class CatalogModelRecommender(
    IModelSizeSource sizeSource,
    ModelMemoryReservePolicy? reservePolicy = null)
{
    private readonly IModelSizeSource sizeSource =
        sizeSource ?? throw new ArgumentNullException(nameof(sizeSource));

    private readonly ModelRecommendationEngine engine = new(reservePolicy);

    public async Task<CatalogRecommendation> RecommendAsync(
        GpuSnapshot gpu,
        IReadOnlyList<ModelCatalogEntry> catalogModels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(catalogModels);

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var model in catalogModels)
        {
            var size = await sizeSource
                .GetDownloadSizeBytesAsync(model.Tag, cancellationToken)
                .ConfigureAwait(false);
            if (size is > 0)
            {
                sizes[model.Tag] = size.Value;
            }
        }

        if (sizes.Count == 0)
        {
            // Without sizes there is nothing to weigh against the adapter. Say so plainly
            // instead of presenting a guess as a recommendation.
            return CatalogRecommendation.Empty with
            {
                AdapterExplanation =
                    "Model sizes could not be retrieved, so no recommendation was computed.",
            };
        }

        var manifestModels = catalogModels
            .Where(model => sizes.ContainsKey(model.Tag))
            .SelectMany(model => model.ContextTokens
                .Distinct()
                .Select(context => new ManifestModel(
                    model.Tag,
                    context,
                    sizes[model.Tag],
                    // The engine treats this as the base weight estimate and adds the
                    // runtime and per-token reserves itself.
                    sizes[model.Tag])))
            .ToArray();

        var recommendation = engine.Recommend(gpu, manifestModels);
        var fits = recommendation.Choices
            .Select(choice => new CatalogModelFit(
                choice.Name,
                choice.ContextTokens,
                choice.IsEnabled,
                (long)choice.SignedDownloadSizeBytes,
                choice.Explanation))
            .ToArray();

        return new CatalogRecommendation(
            fits,
            recommendation.AdapterSelectionExplanation,
            SizesKnown: true);
    }
}
