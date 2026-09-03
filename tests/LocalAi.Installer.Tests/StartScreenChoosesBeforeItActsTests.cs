using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The start screen used to carry a button per errand, so there were four primary actions on
/// one page and no way to look at the four descriptions without one of them already being a
/// click away from happening. It is now one choice and one action, which is the shape every
/// installer's Modify / Repair / Remove page has.
///
/// The rule for what starts selected is stated by count rather than by state: when exactly one
/// errand can be chosen, choosing it is not a question and the screen answers it; when several
/// can, one of them deletes things and the person decides.
/// </summary>
public sealed class StartScreenChoosesBeforeItActsTests : IDisposable
{
    private readonly RemovalFixture machine = new();
    private readonly InstallerLanguage original = InstallerCulture.Current;

    public void Dispose()
    {
        InstallerCulture.Current = original;
        machine.Dispose();
    }

    [Fact]
    public void With_several_errands_available_nothing_is_chosen_for_you()
    {
        var start = Installed();

        Assert.Null(start.Selected);
        Assert.False(start.HasSelection);
    }

    [Fact]
    public void Choosing_an_errand_makes_it_the_one_that_will_run()
    {
        var start = Installed();

        start.Select(StartChoice.Remove);

        Assert.Equal(StartChoice.Remove, start.Selected);
        Assert.True(start.HasSelection);
        Assert.True(start.Option(StartChoice.Remove).IsSelected);
    }

    /// <summary>One choice, so choosing another has to release the first.</summary>
    [Fact]
    public void Choosing_a_second_errand_releases_the_first()
    {
        var start = Installed();
        start.Select(StartChoice.Remove);

        start.Select(StartChoice.UpdateOrRepair);

        Assert.False(start.Option(StartChoice.Remove).IsSelected);
        Assert.True(start.Option(StartChoice.UpdateOrRepair).IsSelected);
    }

    /// <summary>
    /// An errand that cannot run must not become the one that will. The row is not focusable on
    /// screen, but the view model is what decides, and a screen reader can reach further than a
    /// mouse.
    /// </summary>
    [Fact]
    public void An_errand_that_is_out_of_reach_cannot_be_chosen()
    {
        var start = Installed();

        start.Select(StartChoice.Install);

        Assert.Null(start.Selected);
    }

    /// <summary>
    /// On a machine with nothing installed only one errand can run, so the screen answers its
    /// own question rather than showing three greyed rows and a dead button.
    /// </summary>
    [Fact]
    public void With_one_errand_available_it_is_chosen_on_arrival()
    {
        var start = Fresh();

        Assert.Equal(StartChoice.Install, start.Selected);
        Assert.True(start.HasSelection);
    }

    /// <summary>
    /// Changing the language rebuilds the rows. Losing the choice in the process would mean
    /// somebody who picked an errand and then switched language watched it un-choose itself.
    /// </summary>
    [Fact]
    public void Changing_the_language_keeps_the_errand_that_was_chosen()
    {
        var start = Installed();
        start.Select(StartChoice.CleanReinstall);

        start.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(StartChoice.CleanReinstall, start.Selected);
        Assert.True(start.Option(StartChoice.CleanReinstall).IsSelected);
    }

    [Fact]
    public void The_screen_announces_that_an_errand_became_choosable()
    {
        var start = Installed();
        var announced = new List<string>();
        start.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        start.Select(StartChoice.Remove);

        Assert.Contains(nameof(InstallerStartViewModel.HasSelection), announced);
    }

    /// <summary>
    /// The reason an errand is out of reach names a sibling row, and on an unrecognised
    /// installation that row is called "Repair this installation" rather than "Update or
    /// repair" — so the sentence sent the reader to a label that was not on the screen.
    /// </summary>
    [Fact]
    public void The_reason_names_the_row_that_is_actually_there()
    {
        InstallerCulture.Current = InstallerLanguage.English;
        var start = Unrecognised();

        var repair = start.Option(StartChoice.UpdateOrRepair).Title;

        Assert.Equal("Repair this installation", repair);
        Assert.Contains(repair, start.Option(StartChoice.Install).UnavailableReason, StringComparison.Ordinal);
    }

    private InstallerStartViewModel Installed() =>
        new(
            machine.LocalAppData,
            readInstalledVersion: () => new InstalledVersion("467ed5f0f9bf", "0.1.51"));

    private InstallerStartViewModel Fresh() =>
        new(
            machine.LocalAppData,
            new StubInspector(ExistingLocalAiState.Absent),
            () => new InstalledVersion(null, null));

    private InstallerStartViewModel Unrecognised() =>
        new(
            machine.LocalAppData,
            new StubInspector(ExistingLocalAiState.Unrecognized),
            () => new InstalledVersion("467ed5f0f9bf", null));

    private sealed class StubInspector(ExistingLocalAiState state) : IExistingLocalAiInspector
    {
        public ExistingLocalAiSnapshot Inspect(string localAppData) =>
            new(state, null, null, state == ExistingLocalAiState.Unrecognized ? "unreadable" : null);
    }
}
