using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Which of the two palettes to paint, given what was chosen and what Windows says.
///
/// The resolution is separated from the registry read so it can be stated as a table. What the
/// registry returns on this machine is not something a test can arrange, and a test that read
/// it would assert whatever this machine happens to be set to — passing either way.
/// </summary>
public sealed class ThemeResolutionTests
{
    [Theory]
    [InlineData(InstallerTheme.Light, true, false)]
    [InlineData(InstallerTheme.Light, false, false)]
    [InlineData(InstallerTheme.Dark, true, true)]
    [InlineData(InstallerTheme.Dark, false, true)]
    public void An_explicit_choice_outranks_the_system(
        InstallerTheme chosen,
        bool systemPrefersDark,
        bool expectedDark)
    {
        Assert.Equal(expectedDark, InstallerThemes.IsDark(chosen, systemPrefersDark));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Following_the_system_means_following_it(bool systemPrefersDark, bool expectedDark)
    {
        Assert.Equal(
            expectedDark,
            InstallerThemes.IsDark(InstallerTheme.System, systemPrefersDark));
    }

    /// <summary>
    /// The registry value is `AppsUseLightTheme`: 0 is dark, 1 is light, and absent is light —
    /// Windows only writes it once somebody has changed the setting, so a machine nobody has
    /// touched has no value at all rather than a light one.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    [InlineData(7, false)]
    public void The_registry_value_reads_zero_as_dark_and_everything_else_as_light(
        int? value,
        bool expectedDark)
    {
        Assert.Equal(expectedDark, InstallerThemes.PrefersDark(value));
    }
}
