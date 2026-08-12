using System.Text;

namespace LocalAi.ReleaseSigner;

public sealed record ReleaseNotesState(
    bool EnglishExists,
    bool RussianExists,
    bool StillTemplate,
    IReadOnlyList<string> Problems)
{
    public bool ReadyToPublish =>
        EnglishExists && RussianExists && !StillTemplate && Problems.Count == 0;
}

/// <summary>
/// The two note files a release is described by, and the checks that keep them honest.
///
/// The version appears in both filenames and in both headings, and nothing has ever compared
/// them. A heading left over from a copied file is invisible in review — the diff is all new
/// lines — and it is what the GitHub release body carries, so the release page can announce a
/// version other than the one it publishes.
///
/// The notes themselves are written by hand and always will be. They are connected prose about
/// what changed and why, and a list of commit subjects is not a worse version of that, it is a
/// different and much less useful thing. What can be automated is the scaffolding and the
/// refusal to publish a scaffold.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>
    /// The marker a scaffolded file carries until someone replaces it. It is a visible sentence
    /// rather than an HTML comment on purpose: if the refusal below is ever bypassed, a reader
    /// of the release page sees the placeholder instead of nothing.
    /// </summary>
    public const string TemplateMarker = "TODO: describe this release before publishing it.";

    public static string EnglishPath(string repositoryRoot, ReleaseVersion version) =>
        Path.Combine(repositoryRoot, "docs", "releases", $"{version}.md");

    public static string RussianPath(string repositoryRoot, ReleaseVersion version) =>
        Path.Combine(repositoryRoot, "docs", "releases", $"{version}.ru.md");

    public static ReleaseNotesState Inspect(string repositoryRoot, ReleaseVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(version);
        var englishPath = EnglishPath(repositoryRoot, version);
        var russianPath = RussianPath(repositoryRoot, version);
        var englishExists = File.Exists(englishPath);
        var russianExists = File.Exists(russianPath);
        var problems = new List<string>();
        var stillTemplate = false;

        if (englishExists)
        {
            var text = File.ReadAllText(englishPath);
            stillTemplate |= text.Contains(TemplateMarker, StringComparison.Ordinal);
            CheckHeading(text, version, englishPath, problems);
            if (!text.Contains($"({version}.ru.md)", StringComparison.Ordinal))
            {
                problems.Add(
                    $"{englishPath} does not link to its Russian counterpart {version}.ru.md.");
            }
        }

        if (russianExists)
        {
            var text = File.ReadAllText(russianPath);
            stillTemplate |= text.Contains(TemplateMarker, StringComparison.Ordinal);
            CheckHeading(text, version, russianPath, problems);
        }

        return new ReleaseNotesState(
            englishExists,
            russianExists,
            stillTemplate,
            problems.AsReadOnly());
    }

    /// <summary>
    /// Writes the scaffold for whichever of the two files is missing, and never touches one that
    /// already exists. A release that is being retried must not lose the notes written for the
    /// first attempt.
    /// </summary>
    public static IReadOnlyList<string> Scaffold(string repositoryRoot, ReleaseVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(version);
        var created = new List<string>();
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs", "releases"));
        var englishPath = EnglishPath(repositoryRoot, version);
        var russianPath = RussianPath(repositoryRoot, version);
        if (!File.Exists(englishPath))
        {
            File.WriteAllText(englishPath, English(version), new UTF8Encoding(false));
            created.Add(englishPath);
        }

        if (!File.Exists(russianPath))
        {
            File.WriteAllText(russianPath, Russian(version), new UTF8Encoding(false));
            created.Add(russianPath);
        }

        return created.AsReadOnly();
    }

    private static void CheckHeading(
        string text,
        ReleaseVersion version,
        string path,
        List<string> problems)
    {
        var heading = text
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.TrimStart('﻿').StartsWith("# ", StringComparison.Ordinal));
        var expected = $"# LocalAi {version}";
        if (heading?.TrimStart('﻿') != expected)
        {
            problems.Add(
                $"{path} is headed '{heading ?? "(nothing)"}' rather than '{expected}'. " +
                "A heading copied from an earlier release is what the release page publishes.");
        }
    }

    private static string English(ReleaseVersion version) =>
        $"""
         # LocalAi {version}

         [Русская версия]({version}.ru.md)

         {TemplateMarker}

         ## Upgrading

         ## Verification

         """;

    private static string Russian(ReleaseVersion version) =>
        $"""
         # LocalAi {version}

         [English version]({version}.md)

         {TemplateMarker}

         ## Обновление

         ## Проверка

         """;
}
