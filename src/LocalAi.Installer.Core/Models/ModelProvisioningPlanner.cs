using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Planning;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Models;

public enum ModelProvisioningMode
{
    None,
    Automatic,
    Exact,
}

public sealed record ModelProvisioningSelection(
    ModelProvisioningMode Mode,
    string? Model = null,
    int ContextTokens = 0)
{
    public static ModelProvisioningSelection None { get; } = new(ModelProvisioningMode.None);
}

public sealed record ModelProvisioningPlan(
    IReadOnlyList<BrokerModelInstallRequest> Requests,
    IReadOnlyList<string> Excluded);

/// <summary>
/// Turns a model choice into requests the broker installer will actually accept.
///
/// The installer refuses anything it cannot tie back to the signed manifest: the model and
/// its context size must appear there exactly once, and every request must carry the
/// recommendation choices that were computed from that same manifest against one adapter
/// budget. So the sizes here come from the release manifest rather than from the routing
/// catalogue or the registry — those two are what the wizard shows a human, and they are
/// not signed.
///
/// Models already present on the machine are not filtered out here on purpose. The broker
/// installer asks the machine what is installed and pulls only what is missing, and that
/// answer is authoritative in a way a pre-computed list is not.
/// </summary>
public static class ModelProvisioningPlanner
{
    public static ModelProvisioningPlan Create(
        IReadOnlyList<ManifestModel> signedModels,
        GpuSnapshot gpu,
        ModelProvisioningSelection selection)
    {
        ArgumentNullException.ThrowIfNull(signedModels);
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Mode == ModelProvisioningMode.None)
        {
            return new([], []);
        }

        if (signedModels.Count == 0)
        {
            return new(
                [],
                ["The release manifest lists no models, so none can be installed from it."]);
        }

        var recommendation = new ModelRecommendationEngine().Recommend(gpu, signedModels);
        if (recommendation.AdapterSelectionStatus != AdapterSelectionStatus.Selected)
        {
            return new([], [recommendation.AdapterSelectionExplanation]);
        }

        // Every request carries the full choice set: the installer requires one shared
        // adapter budget across them and uses the rest to suggest a smaller context when the
        // selected one is refused.
        var choices = recommendation.Choices;
        var excluded = new List<string>();
        List<ModelRecommendationChoice> selected;
        if (selection.Mode == ModelProvisioningMode.Exact)
        {
            selected = ExactChoice(choices, selection, out var exclusion);
            if (exclusion is not null)
            {
                excluded.Add(exclusion);
            }
        }
        else
        {
            selected = AutomaticChoices(choices);
            excluded.AddRange(AutomaticExclusions(choices));
        }

        var requests = new List<BrokerModelInstallRequest>(selected.Count);
        var index = 0;
        foreach (var choice in selected)
        {
            requests.Add(new BrokerModelInstallRequest(
                new ModelInstallAction(
                    // Ordinal rather than the tag: the identifier must be an ASCII token
                    // without a colon, and every catalogue tag has one.
                    $"model-{++index}",
                    choice.Name,
                    choice.ContextTokens,
                    Selected: true,
                    ConsentGranted: true),
                choices));
        }

        return new(requests, excluded);
    }

    private static List<ModelRecommendationChoice> AutomaticChoices(
        IReadOnlyList<ModelRecommendationChoice> choices) =>
        [.. choices
            .Where(choice => choice.IsEnabled)
            .GroupBy(choice => choice.Name, StringComparer.Ordinal)
            // The largest context the adapter can hold, so a model is not installed in a
            // shape the machine could have bettered for free.
            .Select(group => group.OrderByDescending(choice => choice.ContextTokens).First())
            .OrderBy(choice => choice.Name, StringComparer.Ordinal)];

    private static IEnumerable<string> AutomaticExclusions(
        IReadOnlyList<ModelRecommendationChoice> choices) =>
        choices
            .GroupBy(choice => choice.Name, StringComparer.Ordinal)
            .Where(group => group.All(choice => !choice.IsEnabled))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
                $"{group.Key}: does not fit the adapter at any context size, so it was not " +
                "downloaded.");

    private static List<ModelRecommendationChoice> ExactChoice(
        IReadOnlyList<ModelRecommendationChoice> choices,
        ModelProvisioningSelection selection,
        out string? exclusion)
    {
        var match = choices.SingleOrDefault(choice =>
            string.Equals(choice.Name, selection.Model, StringComparison.Ordinal) &&
            choice.ContextTokens == selection.ContextTokens);
        if (match is null)
        {
            exclusion =
                $"{selection.Model} at {selection.ContextTokens} tokens is not in the signed " +
                "release manifest, so it was not downloaded.";
            return [];
        }

        if (!match.IsEnabled)
        {
            exclusion = $"{match.Name} at {match.ContextTokens} tokens: {match.Explanation}";
            return [];
        }

        exclusion = null;
        return [match];
    }
}
