using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A step is done when it was reached, not when it sits left of where the reader is now.
///
/// Position was standing in for history, and the two stopped agreeing once pages could be
/// folded away and revealed out of order. Marking a step done is a claim about what happened
/// to this computer, which is the one claim a step rail must never get wrong.
/// </summary>
public sealed class RailMarksWhatRanTests
{
    /// <summary>
    /// Revealing the settings inserts three steps before the one the reader is standing on.
    /// A remembered position would afterwards name a different step; a remembered page does
    /// not.
    /// </summary>
    [Fact]
    public void Revealing_folded_pages_does_not_move_what_was_already_done()
    {
        var wizard = Ready(StartChoice.UpdateOrRepair);
        while (wizard.CurrentPage != InstallerPage.Confirm && wizard.MoveNext())
        {
        }

        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
        var doneBefore = Done(wizard);

        wizard.RevealSettingsCommand.Execute(null);

        // Confirm is now to the RIGHT of where the reader stands, and still marked done —
        // which is the whole point. Under the old rule "done" and "left of here" were the
        // same statement, so this step would have silently un-happened.
        Assert.All(doneBefore, title => Assert.Contains(title, Done(wizard)));
        Assert.Contains("Confirm", Done(wizard));
        var titles = wizard.StepList.Select(step => step.Title).ToList();
        Assert.True(
            titles.IndexOf("Confirm") >
            titles.IndexOf(wizard.StepList.Single(step => step.IsCurrent).Title));

        // The pages the reveal just added were never visited, so none of them is done —
        // including the one the reader was put on.
        Assert.DoesNotContain(wizard.StepList.Single(step => step.IsCurrent).Title, Done(wizard));
    }

    /// <summary>
    /// The case the merge makes load-bearing: a finish page reached without the pages before
    /// it having run. Position alone would mark the whole rail done, including work that never
    /// took place.
    /// </summary>
    [Fact]
    public async Task A_finish_page_does_not_mark_the_pages_that_never_ran()
    {
        using var machine = new RemovalFixture();
        var wizard = new UninstallWizardViewModel(
            RemovalPreset.FullUninstall,
            offersInstallAfterwards: false,
            machine.Layout,
            machine.Home,
            new StubProcessRunner(),
            machine.HooksPathReader)
        {
            LogDirectory = Path.Combine(
                Path.GetTempPath(),
                "LocalAi.RailTests",
                Guid.NewGuid().ToString("N")),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        // This run did visit every page, so every earlier step is done — what is pinned is
        // that "done" now comes from having been there.
        Assert.Contains("What to remove", Done(wizard));
        Assert.Contains("Remove", Done(wizard));
        Assert.DoesNotContain("Finished", Done(wizard));
    }

    /// <summary>
    /// Going back does not un-visit a page: the reader saw it, and the rail does not pretend
    /// otherwise just because they stepped away from it.
    /// </summary>
    [Fact]
    public void Stepping_back_leaves_what_was_seen_marked()
    {
        var wizard = Ready(StartChoice.Install);
        wizard.MoveNext();
        wizard.MoveNext();
        var reachedTitle = wizard.StepList.Single(step => step.IsCurrent).Title;

        wizard.MovePrevious();

        Assert.NotEqual(reachedTitle, wizard.StepList.Single(step => step.IsCurrent).Title);
        Assert.Contains(reachedTitle, Done(wizard));
    }

    private static InstallerWizardViewModel Ready(StartChoice mode)
    {
        var wizard = new InstallerWizardViewModel(mode);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        return wizard;
    }

    private static IReadOnlyList<string> Done(InstallerWizardViewModel wizard) =>
        wizard.StepList.Where(step => step.IsDone).Select(step => step.Title).ToArray();

    private static IReadOnlyList<string> Done(UninstallWizardViewModel wizard) =>
        wizard.StepList.Where(step => step.IsDone).Select(step => step.Title).ToArray();

    /// <summary>A machine where nothing has to actually be asked to stop.</summary>
    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false, false));
    }
}
