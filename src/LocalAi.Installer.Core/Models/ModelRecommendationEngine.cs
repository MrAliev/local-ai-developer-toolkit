using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Models;

public sealed partial class ModelRecommendationEngine
{
    private const long MaximumModelSize = 1024L * 1024 * 1024 * 1024;
    private readonly ModelMemoryReservePolicy reservePolicy;

    public ModelRecommendationEngine(ModelMemoryReservePolicy? reservePolicy = null)
    {
        this.reservePolicy = reservePolicy ??
            ModelMemoryReservePolicy.ConservativeProduction;
    }

    public ModelRecommendation Recommend(
        GpuSnapshot gpu,
        IEnumerable<ManifestModel> catalog,
        string? manualAdapterStableId = null,
        ModelSelection? manualModel = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(catalog);

        var models = catalog.ToArray();
        if (models.Any(model => model is null))
        {
            throw new ArgumentException("The signed model catalog contains a null entry.", nameof(catalog));
        }

        var eligibleAdapters = SnapshotEligibleAdapters(gpu);
        var adapterSelection = SelectAdapter(
            gpu.State,
            eligibleAdapters,
            manualAdapterStableId);
        var duplicateKeys = FindDuplicateModelKeys(models!);
        var choices = models!
            .Select(model => CreateChoice(
                model,
                duplicateKeys.Contains(SemanticKey(model)),
                adapterSelection.Adapter,
                adapterSelection.Status))
            .OrderBy(choice => choice.RequiredBytes)
            .ThenBy(choice => choice.Name, StringComparer.Ordinal)
            .ThenBy(choice => choice.ContextTokens)
            .ToArray();

        var enabled = choices.Where(choice => choice.IsEnabled).ToArray();
        var minimal = enabled.Length == 0 ? null : enabled[0];
        var recommended = enabled.Length == 0 ? null : enabled[(enabled.Length - 1) / 2];
        var extended = enabled.Length == 0 ? null : enabled[^1];
        var manualSelection = SelectManualModel(choices, manualModel);

        return new ModelRecommendation(
            adapterSelection.Status,
            adapterSelection.Explanation,
            adapterSelection.Adapter,
            choices,
            minimal,
            recommended,
            extended,
            manualSelection.Status,
            manualSelection.Explanation,
            manualSelection.Choice);
    }

    private static GpuAdapterSnapshot[] SnapshotEligibleAdapters(GpuSnapshot gpu)
    {
        if (gpu.State != ObservationState.Available)
        {
            return [];
        }

        var eligible = gpu.Adapters
            .Where(adapter => adapter is not null &&
                !adapter.IsSoftware &&
                adapter.DedicatedLocalBytes > 0 &&
                !string.IsNullOrWhiteSpace(adapter.StableId))
            .Select(adapter => new GpuAdapterSnapshot(
                adapter.StableId,
                adapter.Name,
                adapter.DedicatedLocalBytes,
                false))
            .ToArray();
        var duplicate = eligible
            .GroupBy(adapter => adapter.StableId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"GPU snapshot contains duplicate StableId '{duplicate.Key}'.",
                nameof(gpu));
        }

        return eligible;
    }

    private static AdapterSelection SelectAdapter(
        ObservationState state,
        IReadOnlyList<GpuAdapterSnapshot> eligible,
        string? manualStableId)
    {
        if (manualStableId is not null)
        {
            var selected = eligible.SingleOrDefault(adapter =>
                string.Equals(adapter.StableId, manualStableId, StringComparison.Ordinal));
            return selected is null
                ? new AdapterSelection(
                    AdapterSelectionStatus.InvalidManualSelection,
                    null,
                    $"Manual GPU adapter '{manualStableId}' is not an eligible exact StableId selection; no fallback was used.")
                : new AdapterSelection(
                    AdapterSelectionStatus.Selected,
                    selected,
                    $"Using manually selected dedicated GPU adapter '{selected.StableId}'.");
        }

        var defaultAdapter = eligible
            .OrderByDescending(adapter => adapter.DedicatedLocalBytes)
            .ThenBy(adapter => adapter.StableId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (defaultAdapter is not null)
        {
            return new AdapterSelection(
                AdapterSelectionStatus.Selected,
                defaultAdapter,
                $"Using dedicated GPU adapter '{defaultAdapter.StableId}' with the largest dedicated local VRAM.");
        }

        return new AdapterSelection(
            AdapterSelectionStatus.NoEligibleAdapter,
            null,
            state == ObservationState.Available
                ? "No eligible non-software GPU adapter with dedicated local VRAM is available."
                : $"GPU observation state is {state}; no eligible dedicated GPU adapter is available.");
    }

    private ModelRecommendationChoice CreateChoice(
        ManifestModel model,
        bool isDuplicate,
        GpuAdapterSnapshot? adapter,
        AdapterSelectionStatus adapterStatus)
    {
        var baseEstimate = model.EstimatedVramBytes > 0
            ? (ulong)model.EstimatedVramBytes
            : 0;
        var contextReserve = SaturatingMultiply(
            model.ContextTokens > 0 ? (ulong)model.ContextTokens : 0,
            reservePolicy.ContextBytesPerToken,
            out var contextOverflow);
        var required = SaturatingAdd(
            baseEstimate,
            reservePolicy.FixedRuntimeReserveBytes,
            out var runtimeOverflow);
        required = SaturatingAdd(
            required,
            contextReserve,
            out var totalOverflow);
        var overflow = contextOverflow || runtimeOverflow || totalOverflow;
        var available = adapter?.DedicatedLocalBytes ?? 0;
        var invalidReason = ValidateModel(model, isDuplicate);

        string explanation;
        var enabled = false;
        if (invalidReason is not null)
        {
            explanation = $"Disabled: invalid signed model metadata ({invalidReason}).";
        }
        else if (overflow)
        {
            explanation = "Disabled: memory estimate calculation overflowed and was saturated; broker preflight is required for proof.";
        }
        else if (adapter is null)
        {
            explanation = adapterStatus == AdapterSelectionStatus.InvalidManualSelection
                ? "Disabled: the manual GPU adapter selection is invalid and no fallback was used; this remains an estimate and broker preflight is required for proof."
                : "Disabled: no eligible dedicated GPU adapter is available; this remains an estimate and broker preflight is required for proof.";
        }
        else if (required > available)
        {
            explanation = $"Disabled: estimated required bytes exceed selected-adapter dedicated VRAM by {required - available}; broker preflight is required for proof.";
        }
        else
        {
            enabled = true;
            explanation = "Enabled: estimated to fit selected-adapter dedicated VRAM; broker preflight is required for proof.";
        }

        return new ModelRecommendationChoice(
            model.Name ?? string.Empty,
            model.ContextTokens,
            baseEstimate,
            reservePolicy.FixedRuntimeReserveBytes,
            contextReserve,
            required,
            available,
            enabled ? available - required : 0,
            !overflow && required > available ? required - available : 0,
            enabled,
            explanation);
    }

    private static string? ValidateModel(ManifestModel model, bool isDuplicate)
    {
        if (string.IsNullOrWhiteSpace(model.Name) ||
            model.Name != model.Name.Normalize(NormalizationForm.FormC) ||
            !SafeModelName().IsMatch(model.Name))
        {
            return "invalid model name";
        }

        if (!IsSupportedContext(model.ContextTokens))
        {
            return "invalid supported context token count";
        }

        if (model.DownloadSize is <= 0 or > MaximumModelSize)
        {
            return "invalid download-size estimate";
        }

        if (model.EstimatedVramBytes is <= 0 or > MaximumModelSize)
        {
            return "invalid base VRAM estimate";
        }

        return isDuplicate
            ? "duplicate semantic model name and context"
            : null;
    }

    private static HashSet<string> FindDuplicateModelKeys(
        IReadOnlyList<ManifestModel> models)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            var key = SemanticKey(model);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts
            .Where(pair => pair.Value > 1)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ManualSelection SelectManualModel(
        IReadOnlyList<ModelRecommendationChoice> choices,
        ModelSelection? requested)
    {
        if (requested is null)
        {
            return new ManualSelection(
                ManualModelSelectionStatus.NotRequested,
                null,
                "No manual model selection was requested.");
        }

        var semanticMatches = choices.Where(choice =>
            string.Equals(choice.Name, requested.Name, StringComparison.OrdinalIgnoreCase) &&
            choice.ContextTokens == requested.ContextTokens).ToArray();
        if (semanticMatches.Length > 1)
        {
            return new ManualSelection(
                ManualModelSelectionStatus.Ambiguous,
                null,
                $"Manual model selection '{requested.Name}' at context {requested.ContextTokens.ToString(CultureInfo.InvariantCulture)} is ambiguous because the signed catalog contains semantic duplicates.");
        }

        var exact = choices.SingleOrDefault(choice =>
            string.Equals(choice.Name, requested.Name, StringComparison.Ordinal) &&
            choice.ContextTokens == requested.ContextTokens);
        return exact is null
            ? new ManualSelection(
                ManualModelSelectionStatus.Unknown,
                null,
                $"Manual model selection '{requested.Name}' at context {requested.ContextTokens.ToString(CultureInfo.InvariantCulture)} is unknown; no fallback was used.")
            : new ManualSelection(
                ManualModelSelectionStatus.Selected,
                exact,
                exact.IsEnabled
                    ? "The exact manual model choice is enabled as an estimate; broker preflight is required for proof."
                    : $"The exact manual model choice is preserved but disabled: {exact.Explanation}");
    }

    private static string SemanticKey(ManifestModel model) =>
        $"{(model.Name ?? string.Empty).Normalize(NormalizationForm.FormC)}\u001F{model.ContextTokens.ToString(CultureInfo.InvariantCulture)}";

    private static bool IsSupportedContext(int value) =>
        value is >= 2048 and <= 262144 && (value & (value - 1)) == 0;

    private static ulong SaturatingMultiply(ulong left, ulong right, out bool overflow)
    {
        try
        {
            overflow = false;
            return checked(left * right);
        }
        catch (OverflowException)
        {
            overflow = true;
            return ulong.MaxValue;
        }
    }

    private static ulong SaturatingAdd(ulong left, ulong right, out bool overflow)
    {
        try
        {
            overflow = false;
            return checked(left + right);
        }
        catch (OverflowException)
        {
            overflow = true;
            return ulong.MaxValue;
        }
    }

    private sealed record AdapterSelection(
        AdapterSelectionStatus Status,
        GpuAdapterSnapshot? Adapter,
        string Explanation);

    private sealed record ManualSelection(
        ManualModelSelectionStatus Status,
        ModelRecommendationChoice? Choice,
        string Explanation);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$")]
    private static partial Regex SafeModelName();
}
