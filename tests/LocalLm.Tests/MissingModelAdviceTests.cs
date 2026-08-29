using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

/// <summary>
/// read_image on a machine with no vision model reported "Broker job '&lt;guid&gt;' failed with
/// 'InvalidOperationException'". True, and useless: it named neither the cause nor anything to do
/// about it, while the calibration path in CodeSearch fails closed with an explanation.
///
/// The broker sends back a failure code and no message on purpose, so the sentence has to be
/// assembled where the tool failed — from the routing catalog, which already knows which models
/// each profile runs on.
/// </summary>
public sealed class MissingModelAdviceTests
{
    /// <summary>
    /// The three profiles read_image accepts. VisualAnalysis is the one the reported call used.
    /// </summary>
    [Theory]
    [InlineData(LocalTaskProfile.VisualAnalysis)]
    [InlineData(LocalTaskProfile.Ocr)]
    [InlineData(LocalTaskProfile.ImageTranslation)]
    public void The_advice_names_the_task_and_a_model_that_can_do_it(LocalTaskProfile profile)
    {
        var advice = MissingModelAdvice.ForProfile(profile);

        Assert.Contains(profile.ToString(), advice, StringComparison.Ordinal);
        Assert.Contains("is installed", advice, StringComparison.Ordinal);

        var route = ModelRoutingCatalogResource.LoadDocument().Routes
            .Single(entry => entry.Profile == profile);
        Assert.Contains(route.Candidates[0], advice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Models are only ever installed through the launcher and the broker, so that is the command
    /// the advice gives. Naming an ollama command would tell someone to go around the queue that
    /// keeps ordering, leases and recovery correct.
    /// </summary>
    [Fact]
    public void The_advice_installs_through_the_broker_rather_than_around_it()
    {
        var advice = MissingModelAdvice.ForProfile(LocalTaskProfile.VisualAnalysis);

        Assert.Contains("localai model pull", advice, StringComparison.Ordinal);
        Assert.Contains(
            ModelRoutingCatalogResource.LoadDocument().CatalogVersion,
            advice,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ollama ", advice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Candidates before fallbacks, because that is the order the router would have tried them,
    /// so it is the order worth installing them in.
    /// </summary>
    [Fact]
    public void Candidates_are_offered_before_fallbacks()
    {
        var route = ModelRoutingCatalogResource.LoadDocument().Routes
            .Single(entry => entry.Profile == LocalTaskProfile.VisualAnalysis);
        var advice = MissingModelAdvice.ForProfile(LocalTaskProfile.VisualAnalysis);

        var candidate = advice.IndexOf(route.Candidates[0], StringComparison.Ordinal);
        var fallback = advice.IndexOf(route.Fallbacks[0], StringComparison.Ordinal);

        Assert.True(candidate >= 0 && fallback >= 0);
        Assert.True(
            candidate < fallback,
            $"'{route.Candidates[0]}' should be offered before '{route.Fallbacks[0]}'.");
    }

    /// <summary>
    /// The opposite case has to give the opposite instruction. Telling somebody to install a
    /// model they already have would cost an hour and several gigabytes for nothing.
    /// </summary>
    [Fact]
    public void An_ineligible_request_is_not_told_to_install_anything()
    {
        var advice = MissingModelAdvice.ForIneligibleRequest(LocalTaskProfile.VisualAnalysis);

        Assert.Contains("is installed", advice, StringComparison.Ordinal);
        Assert.DoesNotContain("model pull", advice, StringComparison.Ordinal);
        Assert.Contains("smaller", advice, StringComparison.Ordinal);
    }

    /// <summary>
    /// A profile that runs no model at all still gets a sentence rather than an empty string:
    /// the caller is reporting a failure either way.
    /// </summary>
    [Fact]
    public void A_profile_with_no_models_still_says_something_useful()
    {
        var advice = MissingModelAdvice.ForProfile(LocalTaskProfile.ExactSearch);

        Assert.NotEmpty(advice);
        Assert.Contains("local_models_status", advice, StringComparison.Ordinal);
    }
}
