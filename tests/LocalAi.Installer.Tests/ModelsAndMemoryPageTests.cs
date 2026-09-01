using LocalAi.Contracts;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The page that used to be two.
///
/// The model recommendation depends on the video-memory rule: relaxing it re-computes which
/// models are offered. On separate pages that recompute landed on a page the reader had
/// already left — they answered "which models" under a filter the next page then silently
/// changed. Merged, the rule sits above its consequence, and the consequence moves in view.
/// </summary>
public sealed class ModelsAndMemoryPageTests
{
    [Fact]
    public void The_two_pages_are_one_step_now()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.Install);

        Assert.Equal(8, wizard.StepList.Count);
        Assert.Contains(
            wizard.StepList,
            step => step.Title == "Models and memory");
        Assert.DoesNotContain(wizard.StepList, step => step.Title == "Video memory");
    }

    [Fact]
    public void The_page_states_the_rule_decides_the_list()
    {
        // Both gates a real run passes on the way: the system check, and the required
        // prerequisites being present.
        var wizard = new InstallerWizardViewModel(StartChoice.Install);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        while (wizard.CurrentPage != InstallerPage.Models && wizard.MoveNext())
        {
        }

        Assert.Equal("How models run on this computer", wizard.StepTitle);
        Assert.Contains(
            "Video memory decides which models fit",
            wizard.StepDescription,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The anchor line restates the rule inside the group the rule governs, so the dependency
    /// is legible even when the radio buttons have scrolled out of view.
    /// </summary>
    [Fact]
    public void The_models_group_names_the_rule_it_is_following()
    {
        var page = new ModelsPageViewModel();
        page.ApplyRecommendation(Recommendation(), residencyRequiresVideoMemory: true);

        Assert.Contains("Whole model in video memory", page.RuleSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Relaxing_the_rule_changes_the_line_the_list_follows()
    {
        var page = new ModelsPageViewModel();
        page.ApplyRecommendation(Recommendation(), residencyRequiresVideoMemory: true);
        var strict = page.RuleSummary;

        page.ApplyRecommendation(Recommendation(), residencyRequiresVideoMemory: false);

        Assert.NotEqual(strict, page.RuleSummary);
        Assert.Contains("Relaxed video memory rule", page.RuleSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Relax the setting on the previous page" named a route that the merge removes. The
    /// rule is now above this text on the same page.
    /// </summary>
    [Fact]
    public void Nothing_fitting_points_upward_rather_than_backward()
    {
        var page = new ModelsPageViewModel();
        page.ApplyRecommendation(
            new CatalogRecommendation([], "irrelevant", SizesKnown: true),
            residencyRequiresVideoMemory: true);

        Assert.Contains("Pick a relaxed rule above", page.AutomaticSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("previous page", page.AutomaticSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both settings still reach the list somebody reads before consenting. One page must not
    /// become one line.
    /// </summary>
    [Fact]
    public void Both_halves_still_report_themselves_on_the_review()
    {
        var review = new InstallerWizardViewModel(StartChoice.Install).ReviewText;

        Assert.Contains("Models:", review, StringComparison.Ordinal);
        Assert.Contains("Video memory:", review, StringComparison.Ordinal);
    }

    /// <summary>
    /// The strict rule stays the default even when no adapter was found: the page says the
    /// rule will refuse everything, and leaves the choice to the reader.
    /// </summary>
    [Fact]
    public void No_adapter_does_not_relax_the_rule_for_the_user()
    {
        var page = new ResidencyPageViewModel { HasUsableAdapter = false };

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, page.Policy);
        Assert.True(page.HasAdapterHint);
    }

    private static CatalogRecommendation Recommendation() =>
        new(
            [new CatalogModelFit("qwen3.5:9b", 32768, true, 6_000_000_000, "fits")],
            "irrelevant",
            SizesKnown: true);
}
