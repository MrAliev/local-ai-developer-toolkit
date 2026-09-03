using CodeSearch.Mcp.Resources;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// The CodeSearch tools answer in the reader's language, and stop exactly where a machine starts
/// reading.
///
/// The rule is one sentence: anything to the left of the first colon on a line is a field name
/// and stays English, and so does any status token, enum name, identifier, path or command to
/// the right of one. That is not thrift. The instruction block this product installs into every
/// agent configuration is English-only and keys behaviour on the exact tokens `STALE`,
/// `INITIALIZING` and `CONFIGURED`, and the shipped Russian documentation quotes `CONFIGURED`
/// and `Update:` as literals inside Russian prose — so translating them would break, for a
/// Russian reader alone, the rule their agent was installed with.
/// </summary>
public sealed class SearchOutputFollowsTheReaderTests
{
    [Fact]
    public void Sentences_are_translated()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Equal("Определение не найдено.", CodeSearchText.NoDefinition);
        Assert.Equal("Совпадений нет.", CodeSearchText.NoMatches);
    }

    [Fact]
    public void The_same_sentences_in_English_are_what_every_other_reader_gets()
    {
        Assert.Equal("No definition found.", CodeSearchText.NoDefinition);
        Assert.Equal("No matches.", CodeSearchText.NoMatches);
    }

    /// <summary>
    /// The tokens an agent was told to look for survive the translation of the clause that
    /// explains them. This is the test that would fail if somebody "finished" the job.
    /// </summary>
    [Fact]
    public void Status_tokens_stay_English_in_every_language()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.StartsWith("STALE - ", CodeSearchText.StatusStale("a1b2c3d", "f9e8d7c"), StringComparison.Ordinal);
        Assert.StartsWith("precise ", CodeSearchText.NavigationPrecise, StringComparison.Ordinal);
        Assert.StartsWith("HEURISTIC - ", CodeSearchText.NavigationHeuristicMissing("R:\\repo"), StringComparison.Ordinal);
        Assert.StartsWith("NOT REFRESHED - ", CodeSearchText.StatusNotRefreshed(900, 200), StringComparison.Ordinal);
    }

    /// <summary>
    /// A command a reader is meant to run is the same command in every language, including the
    /// tool names inside the sentence around it.
    /// </summary>
    [Fact]
    public void Commands_and_tool_names_are_never_translated()
    {
        using var reading = TestCulture.Reading("ru");

        var advice = CodeSearchText.NavigationHeuristicMissing("R:\\repo");

        Assert.Contains(
            "localai-launcher.exe run localai sync --root R:\\repo",
            advice,
            StringComparison.Ordinal);
        Assert.Contains("go_to_definition", advice, StringComparison.Ordinal);
        Assert.Contains("find_references", advice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Numbers are formatted before they reach a resource, so the language cannot move a
    /// decimal separator into a line an agent quotes verbatim.
    /// </summary>
    [Fact]
    public void Numbers_do_not_move_with_the_language()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Equal("2.5 чанк/с", CodeSearchText.RateChunks("2.5"));
        Assert.Equal("1.2 мин", CodeSearchText.EtaMinutes("1.2"));
    }

    [Fact]
    public void Every_language_carries_every_string()
    {
        var gaps = CodeSearchText.Catalogue.Gaps();

        Assert.True(gaps.Count == 0, string.Join(Environment.NewLine, gaps));
    }
}
