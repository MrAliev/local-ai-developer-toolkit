using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// `localai prune` deletes things, so what it prints is the reader's only account of what went.
///
/// The account has to survive two readers and two modes. A Russian reader gets the prose; the
/// left column stays English so a paste from either machine diffs line for line. And a
/// `--dry-run` has to be unmistakable from a real sweep — the two print the same rows, because
/// the rows are counts rather than claims, and one line at the top says which run this was.
/// </summary>
public sealed class PruneOutputFollowsTheReaderTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-prune-language-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// The report used to be written in the past tense throughout, and `--dry-run` printed it
    /// unchanged: nine lines claiming deletions that had not happened, contradicted only by one
    /// word in capitals at the very end. A reader who stopped reading early was told the opposite
    /// of what occurred.
    /// </summary>
    [Fact]
    public void A_dry_run_says_at_the_top_that_nothing_was_removed()
    {
        var report = PruneCommand.Execute(root, dryRun: true, DateTimeOffset.UtcNow);

        Assert.NotEmpty(report.Lines);
        Assert.Contains("--dry-run:", report.Lines[0], StringComparison.Ordinal);
        Assert.Contains("nothing was removed", report.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// And a real sweep does not carry that line, so its presence is the difference rather than a
    /// word buried at the end.
    /// </summary>
    [Fact]
    public void A_real_sweep_carries_no_dry_run_line()
    {
        var report = PruneCommand.Execute(root, dryRun: false, DateTimeOffset.UtcNow);

        Assert.DoesNotContain(
            report.Lines,
            line => line.Contains("--dry-run:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The rows count what went; they do not assert that it went, because the same rows print
    /// under a dry run where nothing did.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_rows_state_no_verb_in_either_mode(string language)
    {
        using var reading = TestCulture.Reading(language);

        foreach (var dryRun in new[] { true, false })
        {
            foreach (var line in PruneCommand.Execute(root, dryRun, DateTimeOffset.UtcNow)
                         .Lines
                         // The framing line is the exception: saying nothing was removed is
                         // the whole reason it exists.
                         .Where(line => !line.StartsWith("--dry-run:", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("removed", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("dropped", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("удалено", line, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The field names are the skeleton: a Russian paste and an English one still diff line for
    /// line, and the tokens somebody greps for are the same tokens.
    /// </summary>
    [Fact]
    public void The_left_column_is_english_for_a_russian_reader()
    {
        using var reading = TestCulture.Reading("ru");

        var report = PruneCommand.Execute(root, dryRun: false, DateTimeOffset.UtcNow);

        Assert.Contains(
            report.Lines,
            line => line.StartsWith("archive:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The two lines that close the report share no words, because they used to differ by one
    /// word in capitals and that word was all that separated a preview from a deletion.
    /// </summary>
    [Fact]
    public void The_closing_lines_cannot_be_mistaken_for_one_another()
    {
        var reclaimed = CliText.PruneReclaimed("12.5");
        var wouldReclaim = CliText.PruneWouldReclaim("12.5");

        Assert.Contains("--dry-run", wouldReclaim, StringComparison.Ordinal);
        Assert.DoesNotContain("--dry-run", reclaimed, StringComparison.Ordinal);
        Assert.NotEqual(reclaimed, wouldReclaim, StringComparer.Ordinal);
    }

    /// <summary>A count with its noun in front of it, correct for one, two and five alike.</summary>
    [Fact]
    public void A_count_reads_correctly_in_russian_for_any_number()
    {
        using var reading = TestCulture.Reading("ru");

        foreach (var entries in new[] { 1, 2, 5, 21 })
        {
            Assert.Contains(
                $"записей {entries}",
                CliText.PruneQuarantine(entries),
                StringComparison.Ordinal);
        }
    }
}
