using CodeSearch.Core.Resources;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// The last place this product's answer changed language halfway through a sentence.
///
/// `CodeSearch.Core` was the only assembly on a reader's path with no catalogue at all, and its
/// refusals surface verbatim inside answers whose surrounding prose is already translated — as the
/// `{ex.Message}` half of `semantic_navigation_not_ready:` and its siblings. A Russian reader got a
/// Russian answer with an English cause, which is the first thing they would report.
/// </summary>
public sealed class CoreRefusalsFollowTheReaderTests
{
    /// <summary>
    /// The fact worth stating before any assertion about a particular string: the report is not
    /// the same text in both languages. A catalogue that fell back to English everywhere would
    /// pass every other test in this file.
    /// </summary>
    [Fact]
    public void A_refusal_is_not_the_same_text_in_both_languages()
    {
        var english = IndexText.GenerationNotPublished(@"R:\repo");
        using (TestCulture.Reading("ru"))
        {
            Assert.NotEqual(english, IndexText.GenerationNotPublished(@"R:\repo"));
        }
    }

    [Fact]
    public void Every_language_carries_every_string()
    {
        var gaps = IndexText.Catalogue.Gaps();

        Assert.True(gaps.Count == 0, string.Join(Environment.NewLine, gaps));
    }

    /// <summary>
    /// A refusal that names a repair has to name one the reader can type. `localai sync` is a
    /// command, `semantic.sidx` is a file name, and neither is a word — they stay as they are in
    /// every language, exactly as the tool catalogue next door already does it.
    /// </summary>
    [Fact]
    public void The_commands_and_file_names_stay_as_they_are()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Contains("localai sync", IndexText.GenerationNotPublished(@"R:\repo"), StringComparison.Ordinal);
        Assert.Contains("semantic.sidx", IndexText.GenerationWithoutSemanticIndex("abc", @"R:\repo"), StringComparison.Ordinal);
        Assert.Contains("search_code", IndexText.ChunkStaleGeneration, StringComparison.Ordinal);
        Assert.Contains("overlay", IndexText.OverlayMissing(@"R:\repo"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The identifier a refusal is about is the same identifier in both languages: it is what the
    /// reader compares against what they have, and a translated one compares equal to nothing.
    /// </summary>
    [Fact]
    public void What_a_refusal_names_does_not_move_with_the_language()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Contains("a1b2c3", IndexText.SemanticIndexUnreadable("a1b2c3", @"R:\repo"), StringComparison.Ordinal);
        Assert.Contains(@"R:\repo", IndexText.SemanticIndexUnreadable("a1b2c3", @"R:\repo"), StringComparison.Ordinal);
        Assert.Contains(
            "qwen3-embedding:8b-q8_0",
            IndexText.ModelNotCalibrated("qwen3-embedding:8b-q8_0"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The English is load-bearing beyond this file: `README.md` and two existing tests quote
    /// `threshold not calibrated` as a literal. Moving a sentence into a catalogue is not licence
    /// to reword it, and where the wording did change it was for a stated reason.
    /// </summary>
    [Fact]
    public void The_english_a_reader_already_knows_is_unchanged()
    {
        Assert.Contains(
            "Semantic relevance threshold not calibrated for embedding model",
            IndexText.ModelNotCalibrated("m"),
            StringComparison.Ordinal);
    }
}
