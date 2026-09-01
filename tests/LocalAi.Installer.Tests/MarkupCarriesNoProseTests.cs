using System.IO;
using System.Text.RegularExpressions;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Markup cannot ask which language was chosen, so a sentence written straight into a window is
/// a sentence that stays English whatever the reader picked. Every one of them belongs in
/// <see cref="ViewModels.PageLabels"/> or in the view model that owns the page.
///
/// This exists because the sweep that moved 44 of them looked at Text, Content and Header, and
/// the removal window's own Title was none of those — so the window carrying a fully translated
/// page was still called "Remove LocalAi" in the task bar.
/// </summary>
public sealed class MarkupCarriesNoProseTests
{
    /// <summary>
    /// Product names, the separator between the two autonyms, and the autonyms themselves. A
    /// language offers itself in its own words, so neither of those is ever translated.
    /// </summary>
    private static readonly string[] NotProse = ["LocalAi", "English", "Русский", "·"];

    private static readonly Regex Literal = new(
        "(?<attribute>Title|Text|Content|Header|ToolTip)=\"(?<value>[^\"{][^\"]*)\"",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("UninstallWindow.xaml")]
    [InlineData("StartWindow.xaml")]
    public void No_window_writes_a_sentence_into_its_own_markup(string window)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "LocalAi.Installer", window);
        var offenders = new List<string>();

        foreach (var (line, number) in File.ReadLines(path).Select((line, index) => (line, index + 1)))
        {
            foreach (Match match in Literal.Matches(line))
            {
                var value = match.Groups["value"].Value;
                if (NotProse.Contains(value) || !Regex.IsMatch(value, "[A-Za-zА-Яа-я]"))
                {
                    continue;
                }

                offenders.Add($"{window}:{number}  {match.Groups["attribute"].Value}=\"{value}\"");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Text a reader sees, written into markup that cannot translate it:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

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
