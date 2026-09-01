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

    /// <summary>
    /// The line is captured, not recomputed on every read.
    ///
    /// It is built from the installed version on disk, and a clean reinstall deletes that
    /// pointer half way through its own run. Rebuilt each time, the rail would flip from
    /// "0.1.50 → 0.1.51" to "installing 0.1.51" the moment the removal half succeeded —
    /// erasing, mid-run, the only statement of what was there before.
    /// </summary>
    [Theory]
    [InlineData(StartChoice.Install)]
    [InlineData(StartChoice.UpdateOrRepair)]
    [InlineData(StartChoice.CleanReinstall)]
    public void The_version_line_is_captured_rather_than_recomputed(StartChoice mode)
    {
        var wizard = new InstallerWizardViewModel(mode);

        Assert.Same(wizard.VersionContext, wizard.VersionContext);
    }

    /// <summary>
    /// The consent is worded for the run it consents to. "I have reviewed these settings" was
    /// the same sentence on all three errands, and on a reinstall it was the wrong one: what
    /// the person is agreeing to there is a removal followed by an installation, not a set of
    /// settings.
    /// </summary>
    [Theory]
    [InlineData(StartChoice.Install, "installed")]
    [InlineData(StartChoice.UpdateOrRepair, "change")]
    [InlineData(StartChoice.CleanReinstall, "removed")]
    public void The_consent_names_what_this_run_does(StartChoice mode, string expected)
    {
        Assert.Contains(
            expected,
            new InstallerWizardViewModel(mode).ConsentText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// One tick over one list. Two boxes on one page teach people to tick boxes, so the
    /// reinstall consent covers both halves in a single sentence rather than asking twice.
    /// </summary>
    [Fact]
    public void A_reinstall_consents_to_both_halves_at_once()
    {
        var consent = new InstallerWizardViewModel(StartChoice.CleanReinstall).ConsentText;

        Assert.Contains("removed", consent, StringComparison.Ordinal);
        Assert.Contains("installed", consent, StringComparison.Ordinal);
    }
}
