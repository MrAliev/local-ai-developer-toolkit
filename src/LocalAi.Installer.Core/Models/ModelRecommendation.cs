using System.Collections.ObjectModel;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Models;

public enum AdapterSelectionStatus
{
    Selected,
    NoEligibleAdapter,
    InvalidManualSelection,
}

public enum ManualModelSelectionStatus
{
    NotRequested,
    Selected,
    Unknown,
    Ambiguous,
}

public sealed record ModelSelection(string Name, int ContextTokens);

/// <summary>
/// Explicit memory overhead added to the signed manifest's base VRAM estimate.
/// EstimatedVramBytes is treated as the model's base estimate only, so runtime
/// and context reserves are added exactly once. The defaults deliberately favor
/// conservative recommendations: 1 GiB for runtime workspaces and 256 KiB per
/// context token. Broker preflight remains the authoritative full-VRAM check.
/// </summary>
public sealed record ModelMemoryReservePolicy
{
    public const ulong ConservativeFixedRuntimeReserveBytes = 1024UL * 1024 * 1024;
    public const ulong ConservativeContextBytesPerToken = 256UL * 1024;

    public ModelMemoryReservePolicy(
        ulong fixedRuntimeReserveBytes,
        ulong contextBytesPerToken)
    {
        FixedRuntimeReserveBytes = fixedRuntimeReserveBytes;
        ContextBytesPerToken = contextBytesPerToken;
    }

    public ulong FixedRuntimeReserveBytes { get; }

    public ulong ContextBytesPerToken { get; }

    public static ModelMemoryReservePolicy ConservativeProduction { get; } = new(
        ConservativeFixedRuntimeReserveBytes,
        ConservativeContextBytesPerToken);
}

public sealed class ModelRecommendationChoice
{
    internal ModelRecommendationChoice(
        string name,
        int contextTokens,
        ulong signedBaseEstimateBytes,
        ulong runtimeReserveBytes,
        ulong contextReserveBytes,
        ulong requiredBytes,
        ulong availableDedicatedBytes,
        ulong headroomBytes,
        ulong overBudgetBytes,
        bool isEnabled,
        string explanation)
    {
        Name = name;
        ContextTokens = contextTokens;
        SignedBaseEstimateBytes = signedBaseEstimateBytes;
        RuntimeReserveBytes = runtimeReserveBytes;
        ContextReserveBytes = contextReserveBytes;
        RequiredBytes = requiredBytes;
        AvailableDedicatedBytes = availableDedicatedBytes;
        HeadroomBytes = headroomBytes;
        OverBudgetBytes = overBudgetBytes;
        IsEnabled = isEnabled;
        Explanation = explanation;
    }

    public string Name { get; }

    public int ContextTokens { get; }

    public ulong SignedBaseEstimateBytes { get; }

    public ulong RuntimeReserveBytes { get; }

    public ulong ContextReserveBytes { get; }

    public ulong RequiredBytes { get; }

    public ulong AvailableDedicatedBytes { get; }

    public ulong HeadroomBytes { get; }

    public ulong OverBudgetBytes { get; }

    public bool IsEnabled { get; }

    public string Explanation { get; }
}

public sealed class ModelRecommendation
{
    public const string EstimateDisclaimer =
        "Memory values are a conservative estimate, not proof; broker preflight is required before accepting a model.";

    internal ModelRecommendation(
        AdapterSelectionStatus adapterSelectionStatus,
        string adapterSelectionExplanation,
        GpuAdapterSnapshot? selectedAdapter,
        IEnumerable<ModelRecommendationChoice> choices,
        ModelRecommendationChoice? minimal,
        ModelRecommendationChoice? recommended,
        ModelRecommendationChoice? extended,
        ManualModelSelectionStatus manualSelectionStatus,
        string manualSelectionExplanation,
        ModelRecommendationChoice? manualChoice)
    {
        AdapterSelectionStatus = adapterSelectionStatus;
        AdapterSelectionExplanation = adapterSelectionExplanation;
        SelectedAdapter = selectedAdapter is null
            ? null
            : new GpuAdapterSnapshot(
                selectedAdapter.StableId,
                selectedAdapter.Name,
                selectedAdapter.DedicatedLocalBytes,
                selectedAdapter.IsSoftware);
        Choices = new ReadOnlyCollection<ModelRecommendationChoice>(choices.ToArray());
        Minimal = minimal;
        Recommended = recommended;
        Extended = extended;
        ManualSelectionStatus = manualSelectionStatus;
        ManualSelectionExplanation = manualSelectionExplanation;
        ManualChoice = manualChoice;
    }

    public AdapterSelectionStatus AdapterSelectionStatus { get; }

    public string AdapterSelectionExplanation { get; }

    public GpuAdapterSnapshot? SelectedAdapter { get; }

    public IReadOnlyList<ModelRecommendationChoice> Choices { get; }

    /// <summary>
    /// The smallest enabled option. Null when no options are enabled. With one
    /// enabled option, Minimal, Recommended, and Extended reference that option;
    /// with two, Minimal is the smaller option.
    /// </summary>
    public ModelRecommendationChoice? Minimal { get; }

    /// <summary>
    /// The lower-median enabled option. Null when no options are enabled. With
    /// one enabled option, Minimal, Recommended, and Extended reference that
    /// option; with two, Recommended references the same smaller option as
    /// Minimal while Extended references the larger option.
    /// </summary>
    public ModelRecommendationChoice? Recommended { get; }

    /// <summary>
    /// The largest enabled option. Null when no options are enabled. With one
    /// enabled option, Minimal, Recommended, and Extended reference that option;
    /// with two, Extended is the larger option while Minimal and Recommended
    /// reference the smaller option.
    /// </summary>
    public ModelRecommendationChoice? Extended { get; }

    public ManualModelSelectionStatus ManualSelectionStatus { get; }

    public string ManualSelectionExplanation { get; }

    public ModelRecommendationChoice? ManualChoice { get; }

    public string Disclaimer => EstimateDisclaimer;
}
