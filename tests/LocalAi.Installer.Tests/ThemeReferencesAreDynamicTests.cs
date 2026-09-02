using System.IO;
using System.Text.RegularExpressions;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Swapping the palette repaints only what asks the palette every time it draws.
///
/// A literal colour never asks. A StaticResource asks once, when the window is loaded, and
/// keeps the answer — so one StaticResource brush is a permanently light control after a swap,
/// and the failure is silent: everything else changes around it.
///
/// Both rules are checked by reading the markup, because the thing being checked is the markup.
/// A running window would only prove it for the controls that happened to be visible.
/// </summary>
public sealed class ThemeReferencesAreDynamicTests
{
    private static readonly string[] Windows =
        ["MainWindow.xaml", "UninstallWindow.xaml", "StartWindow.xaml"];

    private static readonly Regex Literal = new("#[0-9A-Fa-f]{6}\\b", RegexOptions.Compiled);

    private static readonly Regex Static = new(
        @"\{StaticResource\s+(?<key>[A-Za-z0-9_]+)\s*\}",
        RegexOptions.Compiled);

    private static readonly Regex PaletteKey = new(
        "x:Key=\"(?<key>[^\"]+)\"",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("UninstallWindow.xaml")]
    [InlineData("StartWindow.xaml")]
    public void No_window_writes_a_colour_of_its_own(string window)
    {
        var offenders = Read(window)
            .Split('\n')
            .Select((line, index) => (line, number: index + 1))
            .SelectMany(entry => Literal
                .Matches(entry.line)
                .Select(match => $"{window}:{entry.number}  {match.Value}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A colour written into a window cannot follow the palette:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("UninstallWindow.xaml")]
    [InlineData("StartWindow.xaml")]
    public void No_window_asks_the_palette_only_once(string window)
    {
        var palette = PaletteKeys();
        var offenders = Read(window)
            .Split('\n')
            .Select((line, index) => (line, number: index + 1))
            .SelectMany(entry => Static
                .Matches(entry.line)
                .Where(match => palette.Contains(match.Groups["key"].Value))
                .Select(match => $"{window}:{entry.number}  {match.Value}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A StaticResource brush keeps its first answer and stops following the palette:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The control styles are as much a part of the window as the window: a hardcoded colour in
    /// a ControlTemplate is the same permanently light control, one level down.
    /// </summary>
    [Fact]
    public void The_control_styles_write_no_colours_either()
    {
        var path = Path.Combine(ThemesDirectory(), "Controls.xaml");
        Assert.True(File.Exists(path), $"Expected control styles at {path}");

        Assert.DoesNotMatch(Literal, File.ReadAllText(path));
    }

    private static HashSet<string> PaletteKeys() =>
        [.. PaletteKey
            .Matches(File.ReadAllText(Path.Combine(ThemesDirectory(), "Light.xaml")))
            .Select(match => match.Groups["key"].Value)];

    private static string Read(string window) =>
        File.ReadAllText(Path.Combine(InstallerDirectory(), window));

    private static string ThemesDirectory() => Path.Combine(InstallerDirectory(), "Themes");

    private static string InstallerDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "LocalAi.Installer");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
