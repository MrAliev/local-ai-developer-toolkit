using LocalAi.Cli.Resources;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// `localai update` downloads and verifies a signed release, so what it says about verification is
/// a security statement rather than decoration, and it has to be as precise in Russian as in
/// English.
///
/// The usage block is asserted here rather than in a run because it is what a reader consults
/// before deciding to take an update at all.
/// </summary>
public sealed class UpdateOutputFollowsTheReaderTests
{
    [Fact]
    public void The_usage_block_is_not_the_same_text_in_both_languages()
    {
        var english = CliText.UpdateUsage(30);

        using var reading = TestCulture.Reading("ru");
        var russian = CliText.UpdateUsage(30);

        Assert.NotEqual(english, russian, StringComparer.Ordinal);
    }

    /// <summary>
    /// The options are what the reader types back, and the ceiling on `--wait` comes from the
    /// constant rather than from a number written into the sentence — the same reason the policy
    /// usage takes its interval range as a hole.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_usage_block_keeps_what_the_reader_types(string language)
    {
        using var reading = TestCulture.Reading(language);

        var usage = CliText.UpdateUsage(30);

        Assert.Contains("Usage: localai update [--wait] [--force]", usage, StringComparison.Ordinal);
        Assert.Contains("--wait", usage, StringComparison.Ordinal);
        Assert.Contains("--force", usage, StringComparison.Ordinal);
        Assert.Contains("30", usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both orders of verification, in both languages. The old English said the manifest was
    /// checked "before anything is downloaded", which is not what happens — the manifest and its
    /// signature are fetched first and verified after — and it left out the second check
    /// entirely, which is the one that stops a tampered archive.
    /// </summary>
    [Fact]
    public void The_usage_block_states_both_checks_in_english()
    {
        var usage = CliText.UpdateUsage(30);

        Assert.Contains(
            "verified\r\n  against the embedded release key before the package is downloaded",
            usage.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "the\r\n  package against the manifest before anything is extracted",
            usage.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A count with a noun in front of it, which is how Russian survives having no plural support:
    /// genitive plural is correct for one, two and five alike.
    /// </summary>
    [Fact]
    public void A_queue_count_reads_correctly_in_russian_for_any_number()
    {
        using var reading = TestCulture.Reading("ru");

        foreach (var queued in new[] { 1, 2, 5, 21 })
        {
            Assert.Contains(
                $"заданий {queued}",
                CliText.UpdateQueueWaiting(queued),
                StringComparison.Ordinal);
        }
    }
}
