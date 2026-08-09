using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.IntegrationTests;

/// <summary>
/// Walks the wizard end to end without installing anything.
///
/// This project was in the solution — and required by <c>SolutionShapeTests</c> — with no tests
/// in it at all, so "integration tests for the installer" existed as a name and nothing else.
/// These exercise the real view models against the real environment detector: no package is
/// resolved and <c>RunAsync</c> is never called, so nothing is downloaded and nothing is
/// written outside the wizard's own state.
///
/// What they are for is the class of failure this repository kept hitting: a wizard that
/// reaches its last page and reports success while some step quietly did nothing.
/// </summary>
public sealed class WizardNavigationTests
{
    [Fact]
    public void The_wizard_starts_on_the_first_step_and_holds_until_detection_finishes()
    {
        var wizard = new InstallerWizardViewModel();

        Assert.NotEmpty(wizard.StepList);
        Assert.Equal(0, StepIndex(wizard));
        // Detection has not been given a result yet, so there is nothing to move on from.
        Assert.True(wizard.Diagnose.IsChecking);
        Assert.False(wizard.CanMoveNext);
    }

    [Fact]
    public void Each_step_forward_moves_exactly_one_place_and_reports_it()
    {
        var wizard = Ready();
        var moves = 0;

        while (wizard.CanMoveNext)
        {
            var before = StepIndex(wizard);
            wizard.NextCommand.Execute(null);
            var after = StepIndex(wizard);
            Assert.Equal(before + 1, after);
            Assert.Equal($"Step {after + 1} of {wizard.StepList.Count}", wizard.StepStatus);
            moves++;
        }

        // How far Next gets depends on the machine — the dependency page holds until required
        // items are present or consented, and that is real state this test deliberately does
        // not fake. What is asserted is the invariant that holds everywhere: forward is one
        // place at a time and the reported position agrees with it.
        Assert.True(moves > 0);
        Assert.False(wizard.CanMoveNext);
    }

    [Fact]
    public void The_wizard_cannot_be_run_from_anywhere_but_the_confirmation_page()
    {
        var wizard = Ready();

        while (wizard.CanMoveNext)
        {
            // Running is offered on one page only. A wizard that could start from the middle
            // would apply a plan the user never saw in full.
            Assert.False(wizard.CanRun);
            Assert.False(wizard.IsInstallVisible);
            wizard.NextCommand.Execute(null);
        }
    }

    [Fact]
    public void Walking_back_returns_to_the_first_step()
    {
        var wizard = Ready();
        while (wizard.CanMoveNext)
        {
            wizard.NextCommand.Execute(null);
        }

        while (wizard.CanMovePrevious)
        {
            wizard.BackCommand.Execute(null);
        }

        Assert.Equal(0, StepIndex(wizard));
        Assert.False(wizard.CanMovePrevious);
    }

    [Fact]
    public void The_review_says_the_package_will_not_be_installed_when_none_was_resolved()
    {
        var wizard = Ready();

        // Nothing was resolved, because no release was checked. The wizard must say so rather
        // than let the run finish as "complete" with no package installed — the outcome that
        // InstallPackageAsync calls the one worse than a visible failure.
        Assert.Contains(
            "not resolved",
            wizard.Package.ReviewText,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(wizard.Package.HasPackage);
        // An unresolved package still must not block the other steps.
        Assert.True(wizard.Package.CanContinue);
    }

    [Fact]
    public void A_request_left_at_latest_stays_latest_before_anything_is_checked()
    {
        var wizard = Ready();

        Assert.True(wizard.Package.WantsLatest);
        Assert.Null(wizard.Package.ResolvedTag);
    }

    private static InstallerWizardViewModel Ready()
    {
        var wizard = new InstallerWizardViewModel();
        // The real wizard clears this when the environment probe finishes; supplying the result
        // directly keeps the test off the machine's actual hardware detection.
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(true);
        return wizard;
    }

    private static int StepIndex(InstallerWizardViewModel wizard) => wizard.CurrentPageIndex;
}

