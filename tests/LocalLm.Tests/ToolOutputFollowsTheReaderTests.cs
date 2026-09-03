using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Core.Resources;
using LocalAi.Tests.Shared;

namespace LocalLm.Tests;

/// <summary>
/// The half of the change that is the point of it: a reader whose machine is Russian is answered
/// in Russian, and the same line in English is the same line.
///
/// The rest of this assembly asserts the English wording, because English is what the suite
/// reads in by default and what a machine with no translation gets. Without these, that suite
/// would pass just as happily with the Russian resource deleted.
/// </summary>
public sealed class ToolOutputFollowsTheReaderTests
{
    [Fact]
    public void The_notice_line_is_Russian_for_a_Russian_reader()
    {
        using var reading = TestCulture.Reading("ru");

        var notice = Result(ResidencyShortfall.None, residentPercent: 100).Notice;

        Assert.StartsWith("🔧 Локально: qwen3-coder:30b.", notice, StringComparison.Ordinal);
        Assert.Contains("Сэкономлено", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_line_in_English_says_the_same_thing()
    {
        var notice = Result(ResidencyShortfall.None, residentPercent: 100).Notice;

        Assert.StartsWith("🔧 Local: qwen3-coder:30b.", notice, StringComparison.Ordinal);
        Assert.Contains("Saved", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mark beside the model is the shortest thing on the line and the easiest to leave
    /// behind in one language.
    /// </summary>
    [Fact]
    public void The_residency_mark_and_its_advice_follow_the_reader_too()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Contains(
            "целиком на процессоре",
            Result(ResidencyShortfall.Cpu, residentPercent: 0).Notice,
            StringComparison.Ordinal);
        Assert.Contains(
            "видеопамять",
            new ResidencyAdvice().AdviceFor(ResidencyShortfall.Cpu)!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A language nothing was translated into is answered in English rather than refused, and
    /// this is the case a machine in Germany actually hits.
    /// </summary>
    [Fact]
    public void A_language_with_no_translation_falls_back_to_English()
    {
        using var reading = TestCulture.Reading("de-DE");

        Assert.StartsWith(
            "🔧 Local:",
            Result(ResidencyShortfall.None, residentPercent: 100).Notice,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Numbers do not move with the words. `2,5` in a line an agent quotes verbatim is a change
    /// nobody asked for, and the duration is the number this line always carries.
    /// </summary>
    [Fact]
    public void The_numbers_stay_invariant_in_every_language()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Contains(
            "8.2 с",
            Result(ResidencyShortfall.None, residentPercent: 100).Notice,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A language that exists in the neutral resource and not in the translated one is how a
    /// half-Russian answer gets shipped: every line looks translated until the one that is not.
    /// </summary>
    [Fact]
    public void Every_language_carries_every_string()
    {
        var gaps = LocalLmText.Catalogue.Gaps();

        Assert.True(gaps.Count == 0, string.Join(Environment.NewLine, gaps));
    }

    private static LocalResult Result(ResidencyShortfall? shortfall, int? residentPercent) =>
        new(
            "answer",
            30_000,
            "qwen3-coder:30b",
            LocalLmText.PromptOnly,
            new LocalUsageReceipt(
                JobId: Guid.Empty,
                Tool: "ask_local",
                Operation: "Chat",
                Model: "qwen3-coder:30b",
                QueueDuration: TimeSpan.Zero,
                ExecutionDuration: TimeSpan.FromSeconds(8.2),
                InputCharacters: 0,
                EstimatedCloudTokensSaved: 30_000,
                RepositoryId: null,
                GenerationId: null,
                GitTree: null,
                Routing: shortfall is null
                    ? null
                    : new LocalRoutingReceipt(
                        TaskProfile: null,
                        SelectedModel: "qwen3-coder:30b",
                        ContextTokens: null,
                        WasCold: false,
                        UsedFallback: false,
                        ValidatorResult: null,
                        EstimatedGrossCloudTokensSaved: 30_000,
                        EstimatedVerificationTokens: 0,
                        EstimatedNetCloudTokensSaved: 30_000,
                        ResidencyShortfall: shortfall.Value,
                        VramResidentPercent: residentPercent)));
}
