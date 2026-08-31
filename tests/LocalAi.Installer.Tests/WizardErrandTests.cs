using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The wizard knows which errand the start screen was asked for.
///
/// It did not: `StartWindow` opened the same `MainWindow` for "Install LocalAi" and for
/// "Update or repair", and the view model had no field for the choice — so an update
/// introduced itself as an installation and asked every question an installation asks
/// (#257). Everything else in that issue depends on this one value existing.
/// </summary>
public sealed class WizardErrandTests
{
    [Fact]
    public void An_unstated_errand_is_an_installation()
    {
        Assert.Equal(StartChoice.Install, new InstallerWizardViewModel().Mode);
    }

    [Theory]
    [InlineData(StartChoice.Install)]
    [InlineData(StartChoice.UpdateOrRepair)]
    [InlineData(StartChoice.CleanReinstall)]
    public void The_errand_survives_construction(StartChoice mode)
    {
        Assert.Equal(mode, new InstallerWizardViewModel(mode).Mode);
    }

    /// <summary>
    /// The title bar is the first thing that says what this run is. An update calling itself
    /// "LocalAi Setup" is the same wizard as an install, which is the impression that has to
    /// stop.
    /// </summary>
    [Theory]
    [InlineData(StartChoice.Install, "LocalAi Setup")]
    [InlineData(StartChoice.UpdateOrRepair, "LocalAi — Update or repair")]
    [InlineData(StartChoice.CleanReinstall, "LocalAi — Reinstall")]
    public void The_title_says_what_this_run_is(StartChoice mode, string expected)
    {
        Assert.Equal(expected, new InstallerWizardViewModel(mode).WindowTitle);
    }

    /// <summary>
    /// The version line is present on every page and never empty — a rail that sometimes says
    /// nothing is a rail people stop reading. Its exact content depends on the machine, so
    /// what is pinned here is that it always answers.
    /// </summary>
    [Fact]
    public void The_rail_always_carries_a_version_line()
    {
        var context = new InstallerWizardViewModel(StartChoice.UpdateOrRepair).VersionContext;

        Assert.False(string.IsNullOrWhiteSpace(context));
    }
}
