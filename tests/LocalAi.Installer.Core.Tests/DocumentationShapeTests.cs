namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Every document in this repository exists in English and in Russian, and each one links to
/// the other.
///
/// The rule was written down long before anything checked it, and by the time anyone looked,
/// thirty-eight files under docs/superpowers had a translation and no way to reach it, one
/// document existed in Russian only, and one plan in English only. A rule nothing enforces is
/// a rule that decays quietly — which is exactly what a documentation rule cannot afford,
/// because nobody notices a missing translation until they are the person who needed it.
///
/// Test fixtures are exempt: files under a Fixtures directory are inputs to a test corpus,
/// not documentation, and translating one would change what the corpus measures.
/// </summary>
public sealed class DocumentationShapeTests
{
    private const string RussianSuffix = ".ru.md";
    private const string EnglishLink = "[English version](";
    private const string RussianLink = "[Русская версия](";

    [Fact]
    public void Every_document_has_a_translation()
    {
        var missing = Documents()
            .Where(document => !File.Exists(Pair(document)))
            .Select(Relative)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "These documents exist in one language only: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_document_links_to_its_translation()
    {
        var unlinked = new List<string>();
        foreach (var document in Documents())
        {
            var text = File.ReadAllText(document);
            var expected = Path.GetFileName(Pair(document));
            var marker = document.EndsWith(RussianSuffix, StringComparison.OrdinalIgnoreCase)
                ? EnglishLink
                : RussianLink;
            if (!text.Contains(marker + expected + ")", StringComparison.Ordinal))
            {
                unlinked.Add(Relative(document));
            }
        }

        Assert.True(
            unlinked.Count == 0,
            "These documents do not link to their translation: " +
            string.Join(", ", unlinked.OrderBy(path => path, StringComparer.Ordinal)));
    }

    private static string Pair(string document) =>
        document.EndsWith(RussianSuffix, StringComparison.OrdinalIgnoreCase)
            ? document[..^RussianSuffix.Length] + ".md"
            : document[..^".md".Length] + RussianSuffix;

    private static string Relative(string document) =>
        Path.GetRelativePath(RepositoryRoot(), document).Replace('\\', '/');

    /// <summary>
    /// Every markdown file the repository ships, minus test fixtures and anything under a
    /// build output directory — a published tree can carry a copy of somebody else's readme.
    /// </summary>
    private static IEnumerable<string> Documents()
    {
        var root = RepositoryRoot();
        return Directory
            .EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                return !relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                    !relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
                    !relative.Contains("/Fixtures/", StringComparison.OrdinalIgnoreCase) &&
                    !relative.StartsWith("publish/", StringComparison.OrdinalIgnoreCase) &&
                    !relative.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) &&
                    !relative.StartsWith(".github/", StringComparison.OrdinalIgnoreCase);
            });
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
