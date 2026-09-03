using System.IO;
using System.Text.RegularExpressions;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The wizard writes nothing in one language only — its progress log included.
///
/// The log used to be the documented exception: it and the report beside it stayed English on a
/// Russian run, because they existed to be read against each other. That held right up until
/// somebody had to read one — a person installing in Russian, handed eight pages in their own
/// language and then a record of what actually happened in another.
///
/// This reads the source rather than a run, because most of the paths that write to the log are
/// only reachable by performing an installation, and a message nobody can assert is a message
/// that quietly stays English.
///
/// It checks the whole file rather than the <c>AppendLog</c> call sites, and that is the second
/// version of this test. The first looked only inside those parentheses and only for two Latin
/// words in a row, so it passed while <c>"Finalising."</c> stayed English, while two calls
/// printed a message assembled elsewhere, and while five sentence fragments sat in locals a line
/// above the call that used them. A guard that goes green over eight surviving English strings is
/// worse than no guard, because it is quoted as proof.
/// </summary>
public sealed class RunLogSpeaksRussianTests
{
    /// <summary>
    /// Prose: two Latin words in a row, or one capitalised word ending a sentence. The second
    /// half is what <c>"Finalising."</c> taught it.
    /// </summary>
    private static readonly Regex EnglishProse = new(
        @"^(?:.*[A-Za-z]{2,}[ ,][ ]?[A-Za-z]{2,}.*|[A-Z][a-z]{3,}[.…]?)$",
        RegexOptions.Compiled);

    private static readonly Regex Literal = new(
        @"""(?:[^""\\\n]|\\.)*""",
        RegexOptions.Compiled);

    /// <summary>
    /// Literals that are prose-shaped and are not text anybody reads: format placeholders, the
    /// name of a Windows page used as an identifier, and the two log lines that carry no words
    /// of their own. Each one is named rather than pattern-matched, so adding to this list is a
    /// decision somebody writes down.
    /// </summary>
    private static readonly string[] NotProse =
    [
        "\"yyyyMMdd-HHmmss\"",
        "\"install-{0}.log\"",
    ];

    [Fact]
    public void The_wizard_writes_nothing_in_one_language_only()
    {
        var path = Path.Combine(
            RepositoryRoot(),
            "src",
            "LocalAi.Installer",
            "ViewModels",
            "InstallerWizardViewModel.cs");
        var source = File.ReadAllText(path);
        var picked = PickedRanges(source);
        var offenders = new List<string>();

        foreach (Match literal in Literal.Matches(StripComments(source)))
        {
            var body = literal.Value[1..^1];
            if (!EnglishProse.IsMatch(body) ||
                NotProse.Contains(literal.Value, StringComparer.Ordinal) ||
                picked.Any(range =>
                    literal.Index >= range.Start && literal.Index < range.End))
            {
                continue;
            }

            var line = source[..literal.Index].Count(character => character == '\n') + 1;
            offenders.Add($"InstallerWizardViewModel.cs:{line}  {literal.Value}");
        }

        Assert.True(
            offenders.Count == 0,
            "These are offered to a reader in English only, on a run somebody chose Russian " +
            "for. Each belongs inside an InstallerCulture.Pick:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The character ranges covered by an <c>InstallerCulture.Pick(</c> call, found by balancing
    /// parentheses: every one of these spans several lines and most contain parentheses of their
    /// own.
    /// </summary>
    private static List<(int Start, int End)> PickedRanges(string source)
    {
        const string Call = "InstallerCulture.Pick(";
        var ranges = new List<(int, int)>();
        for (var start = source.IndexOf(Call, StringComparison.Ordinal);
             start >= 0;
             start = source.IndexOf(Call, start + 1, StringComparison.Ordinal))
        {
            var depth = 0;
            var index = start + Call.Length - 1;
            for (; index < source.Length; index++)
            {
                if (source[index] == '(')
                {
                    depth++;
                }
                else if (source[index] == ')' && --depth == 0)
                {
                    break;
                }
            }

            ranges.Add((start, Math.Min(index + 1, source.Length)));
        }

        return ranges;
    }

    /// <summary>
    /// Comments explain the code in English and always will; only what the code hands a reader
    /// is in question here.
    /// </summary>
    private static string StripComments(string source)
    {
        var stripped = new System.Text.StringBuilder(source.Length);
        var lines = source.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            stripped.Append(
                trimmed.StartsWith("//", StringComparison.Ordinal)
                    ? new string(' ', line.Length)
                    : line);
            stripped.Append('\n');
        }

        return stripped.ToString()[..source.Length];
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
