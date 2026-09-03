using System.Windows;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Swapping the palette is one assignment to merged dictionary zero. What these pin is that it
/// stays one assignment: appending instead would leave the old palette in the lookup, and the
/// control styles that come after dictionary zero would keep resolving against it.
/// </summary>
public sealed class InstallerThemeSwitchTests
{
    private static readonly ResourceDictionary LightPalette = Palette("light");
    private static readonly ResourceDictionary DarkPalette = Palette("dark");

    [Fact]
    public void Following_a_dark_system_paints_the_dark_palette()
    {
        var resources = new ResourceDictionary();
        using var themes = Switch(resources, InstallerTheme.System, systemPrefersDark: true);

        themes.Apply();

        Assert.True(themes.IsDark);
        Assert.Same(DarkPalette, Assert.Single(resources.MergedDictionaries));
    }

    [Fact]
    public void An_explicit_light_choice_ignores_a_dark_system()
    {
        var resources = new ResourceDictionary();
        using var themes = Switch(resources, InstallerTheme.Light, systemPrefersDark: true);

        themes.Apply();

        Assert.False(themes.IsDark);
        Assert.Same(LightPalette, Assert.Single(resources.MergedDictionaries));
    }

    /// <summary>
    /// The palette is dictionary zero and the control styles come after it. A second palette
    /// appended to the end would be looked up last, so the swap would appear to do nothing.
    /// </summary>
    [Fact]
    public void Switching_replaces_the_palette_rather_than_adding_one()
    {
        var resources = new ResourceDictionary();
        var styles = new ResourceDictionary();
        using var themes = Switch(resources, InstallerTheme.Light, systemPrefersDark: false);
        themes.Apply();
        resources.MergedDictionaries.Add(styles);

        themes.Choose(InstallerTheme.Dark);

        Assert.Equal(2, resources.MergedDictionaries.Count);
        Assert.Same(DarkPalette, resources.MergedDictionaries[0]);
        Assert.Same(styles, resources.MergedDictionaries[1]);
    }

    /// <summary>
    /// The window's caption is drawn by the desktop manager, not by these resources, so
    /// something outside has to be told. Without this the palette swaps and the title bar
    /// stays white.
    /// </summary>
    [Fact]
    public void Applying_a_theme_announces_which_one_it_was()
    {
        var resources = new ResourceDictionary();
        using var themes = Switch(resources, InstallerTheme.System, systemPrefersDark: true);
        var announced = new List<bool>();
        themes.Applied += (_, dark) => announced.Add(dark);

        themes.Apply();
        themes.Choose(InstallerTheme.Light);

        Assert.Equal([true, false], announced);
    }

    private static InstallerThemeSwitch Switch(
        ResourceDictionary resources,
        InstallerTheme chosen,
        bool systemPrefersDark) =>
        new(
            resources,
            chosen,
            () => systemPrefersDark,
            dark => dark ? DarkPalette : LightPalette);

    private static ResourceDictionary Palette(string name) =>
        new() { { "Name", name } };
}
