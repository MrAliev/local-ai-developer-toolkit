using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// What a long run says while it runs, and — more of the work — when it says nothing.
///
/// The clock is injected, so nothing here waits for a duration. A test that slept ten seconds to
/// see the first line would be asserting how fast this machine is, which proves nothing and
/// reports itself as a fault in the code under test.
/// </summary>
public sealed class LocalRunProgressTests
{
    private readonly StringWriter written = new();
    private DateTimeOffset now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private LocalRunProgress Progress() => new(written, () => now);

    private void After(int seconds) => now = now.AddSeconds(seconds);

    private string[] Lines() => written.ToString()
        .Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.Length > 0)
        .ToArray();

    /// <summary>
    /// Below ten seconds a person is still waiting rather than wondering, and an agent's log
    /// gains nothing from a line the answer immediately follows.
    /// </summary>
    [Fact]
    public void A_run_that_has_barely_started_says_nothing()
    {
        var progress = Progress();

        for (var second = 0; second < 10; second++)
        {
            progress.Report(new BrokerJobPending(Running: false));
            After(1);
        }

        Assert.Empty(Lines());
    }

    /// <summary>
    /// Ten seconds of silence is where the first line belongs, and it says which of the two
    /// waits this is: a queue that will not move is another client or a stalled broker, and a
    /// long run is a slow model. They are different faults.
    /// </summary>
    [Fact]
    public void Ten_seconds_of_silence_earns_the_first_line()
    {
        var progress = Progress();
        After(10);

        progress.Report(new BrokerJobPending(Running: false));

        // Asserted against the catalogue, so this says "the queued sentence, not the working
        // one" in whichever language the test runs.
        Assert.Equal(CliText.ProgressQueued(10), Assert.Single(Lines()), StringComparer.Ordinal);
    }

    /// <summary>After the first line the rule is thirty seconds of silence, not ten.</summary>
    [Fact]
    public void After_a_line_the_silence_that_earns_the_next_one_is_longer()
    {
        var progress = Progress();
        After(10);
        progress.Report(new BrokerJobPending(Running: false));

        After(29);
        progress.Report(new BrokerJobPending(Running: false));
        Assert.Single(Lines());

        After(1);
        progress.Report(new BrokerJobPending(Running: false));
        Assert.Equal(2, Lines().Length);
    }

    /// <summary>
    /// The number is time in the current state, not since the command started, so the two lines
    /// together read as a timeline: forty seconds queued, then twenty-two working.
    /// </summary>
    [Fact]
    public void The_second_state_is_timed_from_when_it_began()
    {
        var progress = Progress();
        After(10);
        progress.Report(new BrokerJobPending(Running: false));

        After(5);
        progress.Report(new BrokerJobPending(Running: true));
        After(30);
        progress.Report(new BrokerJobPending(Running: true));

        // 35 s since the line before it, but only 30 s since the model started working.
        Assert.Contains("30", Lines()[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("35", Lines()[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A step line is a line, so it resets the silence the heartbeat measures. Two clocks would
    /// double-print, which is the whole reason one object owns this one.
    /// </summary>
    [Fact]
    public void A_step_line_buys_the_same_silence_a_heartbeat_does()
    {
        var progress = Progress();
        After(12);
        progress.Report(new TranslatingFragment(1, 21));

        After(29);
        progress.Report(new BrokerJobPending(Running: true));
        Assert.Single(Lines());

        After(1);
        progress.Report(new BrokerJobPending(Running: true));
        Assert.Equal(2, Lines().Length);
    }

    /// <summary>
    /// The first fragment has nothing to average, so it carries no estimate. The second does,
    /// and the estimate is a mean over what has finished rather than a promise.
    /// </summary>
    [Fact]
    public void The_first_fragment_offers_no_estimate_and_the_second_does()
    {
        var progress = Progress();
        progress.Report(new TranslatingFragment(1, 21));
        var first = Assert.Single(Lines());
        Assert.Contains("21", first, StringComparison.Ordinal);

        After(30);
        progress.Report(new TranslatingFragment(2, 21));

        // Twenty more fragments at thirty seconds each is ten minutes.
        Assert.Contains("10.0", Lines()[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The estimate is a number an agent quotes verbatim, so it is invariant everywhere — a
    /// decimal comma here would be a different number to whoever parses the line.
    /// </summary>
    [Fact]
    public void The_estimate_is_written_the_same_way_on_every_machine()
    {
        using var reading = TestCulture.Reading("ru");
        var progress = Progress();
        progress.Report(new TranslatingFragment(1, 21));
        After(30);
        progress.Report(new TranslatingFragment(2, 21));

        Assert.Contains("10.0", Lines()[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("10,0", Lines()[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The fallback pass re-translates the whole document, so the counter restarts and the
    /// estimate has to start over with it — carrying the old mean would understate a run that
    /// has just doubled.
    /// </summary>
    [Fact]
    public void A_retry_says_so_and_starts_its_estimate_again()
    {
        var progress = Progress();
        progress.Report(new TranslatingFragment(1, 21));
        After(30);
        progress.Report(new TranslatingFragment(2, 21));

        After(5);
        progress.Report(new TranslationRetrying("heading count differs", "qwen2.5-coder:14b"));
        Assert.Contains("qwen2.5-coder:14b", Lines()[^1], StringComparison.Ordinal);

        progress.Report(new TranslatingFragment(1, 21));
        After(6);
        progress.Report(new TranslatingFragment(2, 21));

        // Six seconds a fragment now, not thirty: twenty left is two minutes.
        Assert.Contains("2.0", Lines()[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("10.0", Lines()[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Triage cannot say "of N" until the log ends, so it says the count it has and how long the
    /// run has been going, and the reader infers the rate. An invented denominator would be the
    /// one number here that is a guess.
    /// </summary>
    [Fact]
    public void Triage_counts_without_a_denominator_it_does_not_have()
    {
        var progress = Progress();
        progress.Report(new TriageChoosingModel());

        After(34);
        progress.Report(new TriagingFragment(1));
        Assert.Contains("34", Lines()[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(" 1/", Lines()[^1], StringComparison.Ordinal);

        progress.Report(new TriageMerging(4, 0));
        progress.Report(new TriageLogRead(37));
        Assert.Contains("37", Lines()[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A download is the running heartbeat for the job it belongs to: it prints on the same
    /// silence clock, and "the model is working" would be false beside it — no model is
    /// working, a file is arriving.
    /// </summary>
    [Fact]
    public void A_download_speaks_on_the_same_clock_the_heartbeat_does()
    {
        var progress = Progress();
        After(9);
        progress.Report(new ModelDownloadProgress("downloading", null, 1, 2));
        Assert.Empty(Lines());

        After(1);
        progress.Report(new ModelDownloadProgress("downloading", null, 1, 2));
        Assert.Single(Lines());
    }

    /// <summary>
    /// A change of phase is not a byte update — it answers a different question, and holding
    /// it for the silence clock would leave the reader watching a download line while the run
    /// had moved on to hashing the file.
    /// </summary>
    [Fact]
    public void A_change_of_phase_does_not_wait_for_the_clock()
    {
        var progress = Progress();
        After(10);
        progress.Report(new ModelDownloadProgress("downloading", null, 1, 2));

        After(1);
        progress.Report(new ModelDownloadProgress("verifying", null, 0, 0));

        Assert.Equal(2, Lines().Length);
    }

    /// <summary>
    /// Gigabytes to one decimal, invariant, and both figures rather than a percent: the total
    /// is a sum over the layers named so far, so it grows, and a percent against a growing
    /// denominator goes backwards.
    /// </summary>
    [Fact]
    public void The_download_line_shows_both_figures_in_gigabytes()
    {
        using var reading = TestCulture.Reading("ru");
        var progress = Progress();
        After(10);

        progress.Report(
            new ModelDownloadProgress("downloading", null, 5_046_586_573, 13_743_895_347));

        var line = Assert.Single(Lines());
        Assert.Contains("4.7", line, StringComparison.Ordinal);
        Assert.Contains("12.8", line, StringComparison.Ordinal);
        Assert.DoesNotContain("4,7", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The backend's own word, quoted rather than mapped to one of our phases. Silence here
    /// would recreate this whole defect for every status the backend adds later.
    /// </summary>
    [Fact]
    public void An_unrecognised_phase_is_quoted_with_its_source_named()
    {
        var progress = Progress();
        After(10);

        progress.Report(new ModelDownloadProgress("other", "pulling fs layer", 0, 0));

        var line = Assert.Single(Lines());
        Assert.Contains("pulling fs layer", line, StringComparison.Ordinal);
        Assert.Contains("Ollama", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whole lines, always. A carriage return rewrites the terminal and fills a redirected log
    /// with the same line a hundred times; this console is driven by hooks and agents as often
    /// as by people, and the transcript has to be the same bytes in both.
    /// </summary>
    [Fact]
    public void Every_report_is_a_whole_line_and_never_rewrites_one()
    {
        var progress = Progress();
        progress.Report(new TriageChoosingModel());
        progress.Report(new TranslatingFragment(1, 3));
        After(40);
        progress.Report(new BrokerJobPending(Running: true));

        var text = written.ToString();
        Assert.Equal(3, Lines().Length);
        Assert.DoesNotContain('\r' + "T", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }
}
