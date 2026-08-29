using LocalAi.Contracts;

namespace LocalLm.Core;

/// <summary>
/// What to tell someone whose task has no model to run on.
///
/// The broker records a failure code and nothing else — a raw exception message is not something
/// that boundary hands out — so <c>read_image</c> on a machine with no vision model reported
/// "failed with 'InvalidOperationException'". True, and useless: it named neither the cause nor
/// anything to do about it.
///
/// The routing catalog already knows which models a profile can run on, and it is readable
/// without depending on the broker, which is what <see cref="ModelRoutingCatalogResource"/>
/// exists for. So the answer is assembled here, where the tool that failed can say it.
/// </summary>
public static class MissingModelAdvice
{
    /// <summary>
    /// Named for the profile, listing the models that would satisfy it and the one command that
    /// installs one. The pull goes through the launcher and the broker, which is the only route
    /// models are ever installed by.
    /// </summary>
    public static string ForProfile(LocalTaskProfile profile)
    {
        var models = ModelsFor(profile, out var catalogVersion);
        if (models.Count == 0)
        {
            return $"No local model able to do {profile} is installed, and the routing catalog " +
                "names none for it. Run local_models_status to see what is installed.";
        }

        var install = string.IsNullOrWhiteSpace(catalogVersion)
            ? string.Empty
            : " Install one through the broker: localai-launcher.exe run localai model pull " +
              $"--model {models[0]} --catalog-version {catalogVersion}";

        return $"No local model able to do {profile} is installed. This task runs on " +
            string.Join(", ", models) + "." + install;
    }

    /// <summary>
    /// The opposite case: a model for this task is installed, but none of them can take this
    /// particular request. Telling somebody to install what they already have would waste an hour
    /// and several gigabytes, so this names the two limits that actually decide eligibility.
    /// </summary>
    public static string ForIneligibleRequest(LocalTaskProfile profile)
    {
        var models = ModelsFor(profile, out _);
        var named = models.Count == 0
            ? string.Empty
            : " The models for it are " + string.Join(", ", models) + ".";

        return $"A local model for {profile} is installed, but none can take this request." +
            named +
            " Ask for a smaller context, or — for an image — a smaller one: each model declares " +
            "the context sizes and the pixel count it accepts.";
    }

    private static IReadOnlyList<string> ModelsFor(
        LocalTaskProfile profile,
        out string catalogVersion)
    {
        catalogVersion = string.Empty;
        try
        {
            var document = ModelRoutingCatalogResource.LoadDocument();
            catalogVersion = document.CatalogVersion;
            var route = document.Routes.FirstOrDefault(entry => entry.Profile == profile);
            if (route is null)
            {
                return [];
            }

            // Candidates first, then fallbacks: that is the order the router would have tried
            // them, so it is the order worth installing them in.
            return
            [
                .. route.Candidates
                    .Concat(route.Fallbacks)
                    .Distinct(StringComparer.Ordinal)
            ];
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or
                System.Text.Json.JsonException)
        {
            // Advice is not worth failing over; the caller already has a failure to report.
            return [];
        }
    }
}
