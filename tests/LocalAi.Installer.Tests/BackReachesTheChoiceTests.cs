using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Every choice can be revisited until the run starts. The first page of each wizard was the
/// one place that was not true: Back was dead there, because the screen that asked which errand
/// this is had closed itself on the way out, so there was nothing behind it.
///
/// A wizard opened from Apps and features never saw that screen, and inventing one there would
/// be a different product — so Back on its first page stays unavailable, and the two entries
/// differ because what is behind them differs.
/// </summary>
public sealed class BackReachesTheChoiceTests
{
    [Fact]
    public void The_first_page_goes_back_to_the_screen_that_asked()
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            canReturnToStart: true);

        Assert.Equal(InstallerPage.Diagnose, wizard.CurrentPage);
        Assert.True(wizard.CanMovePrevious);
    }

    /// <summary>
    /// Reached from Apps and features there is no such screen, and a Back that closes the
    /// window and opens something the person never saw is not going back.
    /// </summary>
    [Fact]
    public void With_nothing_behind_it_the_first_page_does_not_offer_back()
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            canReturnToStart: false);

        Assert.False(wizard.CanMovePrevious);
    }

    [Fact]
    public void The_removal_wizard_answers_the_same_way()
    {
        Assert.True(Removal(canReturnToStart: true).CanMovePrevious);
        Assert.False(Removal(canReturnToStart: false).CanMovePrevious);
    }

    /// <summary>
    /// Going back from the first page leaves the wizard rather than moving inside it, so the
    /// window has to be told. Anything else would step to a page that is not in the rail.
    /// </summary>
    [Fact]
    public void Going_back_from_the_first_page_asks_to_leave()
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            canReturnToStart: true);
        var asked = 0;
        wizard.ReturnToStartRequested += (_, _) => asked++;

        wizard.MovePrevious();

        Assert.Equal(1, asked);
        Assert.Equal(InstallerPage.Diagnose, wizard.CurrentPage);
    }

    [Fact]
    public void The_removal_wizard_asks_to_leave_the_same_way()
    {
        var wizard = Removal(canReturnToStart: true);
        var asked = 0;
        wizard.ReturnToStartRequested += (_, _) => asked++;

        wizard.MovePrevious();

        Assert.Equal(1, asked);
        Assert.Equal(UninstallPage.Choose, wizard.CurrentPage);
    }

    private static UninstallWizardViewModel Removal(bool canReturnToStart) =>
        new(
            RemovalPreset.FullUninstall,
            offersInstallAfterwards: false,
            canReturnToStart: canReturnToStart);
}
