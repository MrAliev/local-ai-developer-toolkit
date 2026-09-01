using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

/// <summary>
/// A local call reports how long it took, because the alternative is an invented number.
///
/// The broker has always recorded how long a job waited and how long it ran, and the receipt
/// carrying those reached the line that reports the call — where it was used for the saving and
/// nothing else. So an assistant asked to report the duration had no source for it, in the same
/// sentence that forbids inventing one for the token estimate.
/// </summary>
public sealed class NoticeReportsDurationTests
{
    [Fact]
    public void The_notice_says_how_long_the_call_took()
    {
        var notice = Result(queued: TimeSpan.Zero, ran: TimeSpan.FromSeconds(6.2)).Notice;

        Assert.Contains("6.2 с", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Waiting and running are different stories — one is a queue to look at, the other a
    /// model — so a wait worth mentioning is mentioned separately.
    /// </summary>
    [Fact]
    public void A_long_wait_is_named_apart_from_the_work()
    {
        var notice = Result(
            queued: TimeSpan.FromSeconds(4.1),
            ran: TimeSpan.FromSeconds(2)).Notice;

        Assert.Contains("6.1 с", notice, StringComparison.Ordinal);
        Assert.Contains("в очереди 4.1 с", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// A wait nobody would act on stays out of the line: the ordinary case is short, and a
    /// parenthesis on every call is how a line stops being read.
    /// </summary>
    [Theory]
    [InlineData(0.1, 6.0)]
    [InlineData(0.6, 20.0)]
    public void A_wait_that_changes_nothing_is_left_out(double queued, double ran)
    {
        var notice = Result(
            queued: TimeSpan.FromSeconds(queued),
            ran: TimeSpan.FromSeconds(ran)).Notice;

        Assert.DoesNotContain("в очереди", notice, StringComparison.Ordinal);
    }

    /// <summary>Tenths while that means something, whole seconds once it does not.</summary>
    [Fact]
    public void A_long_call_is_reported_in_whole_seconds()
    {
        var notice = Result(TimeSpan.Zero, TimeSpan.FromSeconds(93.4)).Notice;

        Assert.Contains("93 с", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("93.4", notice, StringComparison.Ordinal);
    }

    /// <summary>The saving is still there: the duration is an addition, not a replacement.</summary>
    [Fact]
    public void The_saving_survives_beside_the_duration()
    {
        var notice = Result(TimeSpan.Zero, TimeSpan.FromSeconds(1), saved: 30_000).Notice;

        Assert.Contains("Сэкономлено", notice, StringComparison.Ordinal);
        Assert.Contains("1.0 с", notice, StringComparison.Ordinal);
        Assert.Contains("qwen3", notice, StringComparison.Ordinal);
    }

    private static LocalResult Result(TimeSpan queued, TimeSpan ran, int saved = 0) =>
        new(
            "answer",
            saved,
            "qwen3-coder:30b",
            "detail",
            new LocalUsageReceipt(
                JobId: Guid.Empty,
                Tool: "ask_local",
                Operation: "Chat",
                Model: "qwen3-coder:30b",
                QueueDuration: queued,
                ExecutionDuration: ran,
                InputCharacters: 0,
                EstimatedCloudTokensSaved: saved,
                RepositoryId: null,
                GenerationId: null,
                GitTree: null));
}
