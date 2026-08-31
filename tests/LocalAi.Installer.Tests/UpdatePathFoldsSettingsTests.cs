using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// What an update stops asking, and what it must still show.
///
/// An update re-asked four questions it already had answers to — prerequisites that are
/// installed, models that are pulled, a residency policy now read from disk, client
/// registrations that exist — and then told the reader it was on "Step 3 of 9" of a nine-page
/// install (#257). The pages fold away; their values still reach the review page, because a
/// folded page must not become an unlisted effect.
/// </summary>
public sealed class UpdatePathFoldsSettingsTests
{
    [Fact]
    public void An_install_visits_every_page()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.Install);

        Assert.False(wizard.AreSettingsFolded);
        Assert.Equal(9, wizard.StepList.Count);
    }

    [Fact]
    public void An_update_folds_the_four_questions_it_has_answers_to()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);

        Assert.True(wizard.AreSettingsFolded);
        // Diagnose, Package, Confirm, Progress, Finish.
        Assert.Equal(5, wizard.StepList.Count);
        Assert.Contains("Step 1 of 5", wizard.StepStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// Diagnose stays on every path: it is where an interrupted run is offered back and where
    /// an unsupported machine is stopped, neither of which an update may skip.
    /// </summary>
    [Fact]
    public void The_first_page_is_the_system_check_on_both_paths()
    {
        Assert.Equal(
            InstallerPage.Diagnose,
            new InstallerWizardViewModel(StartChoice.UpdateOrRepair).CurrentPage);
    }

    [Fact]
    public void Next_on_an_update_goes_straight_from_the_check_to_the_package()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);

        Assert.True(wizard.MoveNext());

        Assert.Equal(InstallerPage.Package, wizard.CurrentPage);
    }

    [Fact]
    public void Back_from_the_package_returns_to_the_check_rather_than_a_folded_page()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);
        wizard.MoveNext();

        Assert.True(wizard.MovePrevious());

        Assert.Equal(InstallerPage.Diagnose, wizard.CurrentPage);
    }

    /// <summary>
    /// Folding is never irrevocable. The run where a carried-forward answer is wrong is
    /// exactly the run somebody discovers it on the review page.
    /// </summary>
    [Fact]
    public void The_folded_pages_come_back_on_request()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);

        wizard.RevealSettings();

        Assert.False(wizard.AreSettingsFolded);
        Assert.Equal(9, wizard.StepList.Count);
        Assert.Equal(InstallerPage.Dependencies, wizard.CurrentPage);
    }

    [Fact]
    public void Revealing_twice_changes_nothing_further()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);
        wizard.RevealSettings();
        wizard.MoveNext();
        var page = wizard.CurrentPage;

        wizard.RevealSettings();

        Assert.Equal(page, wizard.CurrentPage);
    }

    /// <summary>
    /// The review page is the contract: folding a page must not remove its line, or the
    /// wizard would be applying an effect it never listed.
    /// </summary>
    [Theory]
    [InlineData("Dependencies:")]
    [InlineData("Models:")]
    [InlineData("Model residency:")]
    [InlineData("Update check:")]
    public void Every_folded_page_still_reports_itself_on_the_review(string expected)
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);

        Assert.Contains(expected, wizard.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A wizard whose system check has passed, which is what gates Next on the first page.
    /// Without it every navigation assertion here would be testing the gate rather than the
    /// folding.
    /// </summary>
    private static InstallerWizardViewModel Checked(StartChoice mode)
    {
        var wizard = new InstallerWizardViewModel(mode);
        // Both halves of the gate: the probe has finished, and it found a usable machine.
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        return wizard;
    }
}
