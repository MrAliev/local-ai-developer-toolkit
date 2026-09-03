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
    /// Foreground is inherited, and what it is inherited from is the window — whose own default
    /// is the system's near-black. A TextBlock style that sets everything but the colour
    /// therefore paints dark text on a dark page, and the implicit style cannot rescue it: an
    /// element that names a style of its own does not get the implicit one as well.
    ///
    /// This is not hypothetical. The headline of the start window read as black on #1F1F1F in
    /// the first dark build, because its style set the size and the weight and nothing else.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("UninstallWindow.xaml")]
    [InlineData("StartWindow.xaml")]
    public void Every_text_style_names_its_colour(string window)
    {
        var text = Read(window);
        var offenders = Regex
            .Matches(
                text,
                "<Style x:Key=\"(?<key>[^\"]+)\" TargetType=\"TextBlock\">(?<body>.*?)</Style>",
                RegexOptions.Singleline)
            .Where(match => !match.Groups["body"].Value.Contains("Foreground", StringComparison.Ordinal))
            .Select(match => $"{window}  {match.Groups["key"].Value}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A text style that names no colour inherits the system's, which is near-black:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// And the window itself, which is what every unstyled element inherits from.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("UninstallWindow.xaml")]
    [InlineData("StartWindow.xaml")]
    public void Every_window_names_the_colour_its_content_inherits(string window)
    {
        Assert.Contains(
            "Foreground=\"{DynamicResource TextPrimary}\"",
            Read(window),
            StringComparison.Ordinal);
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
