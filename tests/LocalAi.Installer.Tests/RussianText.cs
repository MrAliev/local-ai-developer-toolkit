using System.Text.RegularExpressions;

namespace LocalAi.Installer.Tests;

/// <summary>
/// What "this string is Russian" is allowed to mean in a test.
///
/// The first version asked only for one Cyrillic character anywhere, which passes on
/// "Проверка. Items marked as a warning still allow installation." — a half-translated line is
/// exactly what these tests exist to catch, so that bar was below the thing being measured.
///
/// The bar here is a run of three Latin words in a row. Every Russian line in the installer
/// carries Latin names — LocalAi, winget, localai doctor, release-manifest.json — but names
/// come one or two at a time; three in a row is a clause somebody forgot.
/// </summary>
public static class RussianText
{
    private static readonly Regex Cyrillic = new("[а-яА-ЯёЁ]", RegexOptions.Compiled);

    private static readonly Regex LatinRun = new(
        @"\b[A-Za-z][A-Za-z.\-]*\s+[A-Za-z][A-Za-z.\-]*\s+[A-Za-z][A-Za-z.\-]*\b",
        RegexOptions.Compiled);

    public static void AssertRussian(string text, string what)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), $"{what} is empty");
        Assert.True(Cyrillic.IsMatch(text), $"{what} is still English: {text}");

        var clause = LatinRun.Match(text);
        Assert.False(
            clause.Success,
            $"{what} still carries an English clause \"{clause.Value}\": {text}");
    }
}
