using LocalAi.Cli.Resources;
using LocalAi.Repository;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// What the entry point itself says — the guard's outcome, the argument refusals, and the report
/// `hooks install` gives.
///
/// Two things here are not translation and are asserted for that reason. The guard runs after the
/// culture is resolved, so a reader who chose Russian is owed Russian even when the run was
/// cancelled. And what `hooks install` says about a hook the reader wrote themselves has to be
/// true whichever language it is said in.
/// </summary>
public sealed class EntryPointOutputFollowsTheReaderTests
{
    [Fact]
    public void The_cancelled_line_is_not_the_same_text_in_both_languages()
    {
        var english = CliText.RunCancelled;

        using var reading = TestCulture.Reading("ru");

        Assert.NotEqual(english, CliText.RunCancelled, StringComparer.Ordinal);
    }

    /// <summary>
    /// The two repository refusals answer different questions and must stay distinguishable: "there
    /// is no repository here" and "this path does not exist" send the reader to different places.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_two_repository_refusals_are_different_sentences(string language)
    {
        using var reading = TestCulture.Reading(language);

        Assert.NotEqual(
            CliText.RepositoryOutsideGit(@"C:\somewhere"),
            CliText.RepositoryPathNotDirectory(@"C:\somewhere"),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Only one of them offers `--root`. The other is reached both when nothing was passed and when
    /// `--root` was passed and pointed outside a repository, so naming it there would tell half the
    /// readers to do what they had just done.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Only_the_path_refusal_offers_a_route(string language)
    {
        using var reading = TestCulture.Reading(language);

        Assert.Contains(
            "--root",
            CliText.RepositoryPathNotDirectory(@"C:\somewhere"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--root",
            CliText.RepositoryOutsideGit(@"C:\somewhere"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The option name and the example are typed back, so they are the same in every language.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_inline_limit_refusal_keeps_what_is_typed(string language)
    {
        using var reading = TestCulture.Reading(language);

        var refusal = CliText.SyncInlineLimitInvalid;

        Assert.StartsWith("localai: ", refusal, StringComparison.Ordinal);
        Assert.Contains("--max-inline-files 200", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("--max-inline-files 200.", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The events the message announces are the events the dispatcher accepts — one list, named
    /// twice. #327 made that true of the check; this keeps it true of the sentence.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_unknown_event_message_names_what_is_dispatched(string language)
    {
        using var reading = TestCulture.Reading(language);

        var message = CliText.HookEventUnknown("nonsense", string.Join('|', GitHookLayout.Events));

        foreach (var announced in GitHookLayout.Events)
        {
            Assert.Contains(announced, message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("reference-transaction", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// `hooks install` moves a hook the reader wrote to `.pre-localai` and, until now, said nothing
    /// about it. The sentence has to name the suffix from the constant rather than repeat it, so it
    /// cannot describe a suffix the code does not use.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_chained_line_names_the_suffix_the_code_uses(string language)
    {
        using var reading = TestCulture.Reading(language);

        var line = CliText.HooksChained(GitHookLayout.ChainedSuffix, "post-commit, post-merge");

        Assert.Contains(GitHookLayout.ChainedSuffix, line, StringComparison.Ordinal);
        Assert.Contains("post-commit, post-merge", line, StringComparison.Ordinal);
    }

    /// <summary>A count with its noun in front of it, correct for one, two and five alike.</summary>
    [Fact]
    public void A_hook_count_reads_correctly_in_russian_for_any_number()
    {
        using var reading = TestCulture.Reading("ru");

        foreach (var hooks in new[] { 1, 2, 5, 21 })
        {
            Assert.Contains(
                $"Git-хуков {hooks}",
                CliText.HooksInstalled(hooks, @"C:\repo\.git\hooks"),
                StringComparison.Ordinal);
        }
    }
}
