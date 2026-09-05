using System.Globalization;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>
/// Turns the steps of a long run into console lines, and — most of what it does — decides which
/// steps do not become one.
///
/// One object owns the clock because the two cadence rules share it: a step prints when it
/// happens, and a heartbeat prints only into silence. Two independent timers would double-print,
/// and a reader cannot tell a doubled line from a repeated step.
///
/// Every line is written whole. There is no carriage return anywhere in this product's output,
/// and this is not the place to introduce one: a self-rewriting line looks tidy on a terminal and
/// fills a redirected log with a hundred copies of itself, and this console is driven by hooks
/// and agents at least as often as by people.
/// </summary>
public sealed class LocalRunProgress : ILocalRunObserver
{
    /// <summary>
    /// Below ten seconds a person is still waiting rather than wondering, and a log gains nothing
    /// from a line the answer immediately follows.
    /// </summary>
    private static readonly TimeSpan FirstSilence = TimeSpan.FromSeconds(10);

    /// <summary>Once a run has spoken, silence has to be longer before it is worth breaking.</summary>
    private static readonly TimeSpan LaterSilence = TimeSpan.FromSeconds(30);

    private readonly TextWriter writer;
    private readonly Func<DateTimeOffset> clock;
    private readonly DateTimeOffset startedAt;

    /// <summary>
    /// What the run is about, when a line needs to name it. Only a download does: every
    /// other line here is about the run the reader started a moment ago and can see.
    /// </summary>
    private readonly string subject;

    private string? downloadPhase;
    private string? downloadDetail;

    private DateTimeOffset? lastLineAt;
    private bool? running;
    private DateTimeOffset stateSince;
    private DateTimeOffset? firstFragmentAt;

    public LocalRunProgress(
        TextWriter writer,
        Func<DateTimeOffset> clock,
        string? subject = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.writer = writer;
        this.clock = clock;
        this.subject = subject ?? string.Empty;
        startedAt = clock();
        stateSince = startedAt;
    }

    public void Report(LocalRunStep step)
    {
        var now = clock();
        switch (step)
        {
            case BrokerJobPending pending:
                ReportPending(pending, now);
                break;

            case ModelDownloadProgress download:
                ReportDownload(download, now);
                break;

            case TranslatingFragment fragment:
                ReportFragment(fragment, now);
                break;

            case TranslationRetrying retry:
                // The pass starts over, so the mean the estimate is built from has to as well:
                // carrying the old one across would understate a run that has just doubled.
                firstFragmentAt = null;
                Write(CliText.ProgressTranslationRetry(retry.Detail, retry.Model), now);
                break;

            case TriageChoosingModel:
                Write(CliText.ProgressTriageChoosingModel, now);
                break;

            case TriagingFragment triaged:
                Write(
                    CliText.ProgressTriageFragment(triaged.Index, WholeSeconds(now - startedAt)),
                    now);
                break;

            case TriageLogRead read:
                Write(CliText.ProgressTriageLogRead(read.Fragments), now);
                break;

            case TriageMerging merging:
                Write(CliText.ProgressTriageMerging(merging.Findings, merging.Level), now);
                break;
        }
    }

    /// <summary>
    /// Reported ten times a second, so almost every one of these is silence. The state is read
    /// rather than guessed: a line saying the model is working while the job has not left the
    /// queue is the false reassurance the pair exists to refuse.
    /// </summary>
    private void ReportPending(BrokerJobPending pending, DateTimeOffset now)
    {
        // The first observation happens as the job is enqueued, so the state it reports began
        // when the run did. Only a later change restarts the clock this line reads.
        if (running is not null && running != pending.Running)
        {
            stateSince = now;
        }

        running = pending.Running;
        if (!SilenceEarnsALine(now))
        {
            return;
        }

        var inState = WholeSeconds(now - stateSince);
        Write(
            pending.Running ? CliText.ProgressRunning(inState) : CliText.ProgressQueued(inState),
            now);
    }

    /// <summary>
    /// The running heartbeat for the job it belongs to, which is why nothing else prints
    /// one beside it: "the model is working" would be false of a download.
    ///
    /// A change of phase does not wait for the clock — it answers a different question
    /// from a byte count. The first phase still does, because below ten seconds a reader
    /// is waiting rather than wondering, and a line the answer follows says nothing.
    /// </summary>
    private void ReportDownload(ModelDownloadProgress download, DateTimeOffset now)
    {
        var moved = !string.Equals(downloadPhase, download.Phase, StringComparison.Ordinal) ||
                    !string.Equals(downloadDetail, download.Detail, StringComparison.Ordinal);
        downloadPhase = download.Phase;
        downloadDetail = download.Detail;
        if (!(moved && lastLineAt is not null) && !SilenceEarnsALine(now))
        {
            return;
        }

        var elapsed = WholeSeconds(now - startedAt);
        Write(
            download.Phase switch
            {
                "downloading" => CliText.ProgressPullDownloading(
                    subject,
                    Gigabytes(download.Completed),
                    Gigabytes(download.Total)),
                "preparing" => CliText.ProgressPullPreparing(subject, elapsed),
                "verifying" => CliText.ProgressPullVerifying(elapsed),
                "storing" => CliText.ProgressPullStoring(elapsed),
                _ => CliText.ProgressPullStatus(download.Detail ?? string.Empty, elapsed),
            },
            now);
    }

    /// <summary>
    /// Gibibytes to one decimal, invariant. The same unit the operating system shows for a
    /// file, so the figure matches what the reader will find on disk.
    /// </summary>
    private static string Gigabytes(long bytes) =>
        (bytes / (1024d * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);

    private void ReportFragment(TranslatingFragment fragment, DateTimeOffset now)
    {
        var left = string.Empty;
        if (fragment.Index > 1 && firstFragmentAt is { } first && fragment.Index <= fragment.Total)
        {
            // A mean over what has finished, not a promise: the fragments are whole model calls
            // and their durations differ, which is why the sentence says "about".
            var mean = (now - first) / (fragment.Index - 1);
            var remaining = mean * (fragment.Total - fragment.Index + 1);
            left = CliText.ProgressMinutesLeft(
                remaining.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture));
        }

        if (fragment.Index == 1)
        {
            firstFragmentAt = now;
        }

        Write(
            CliText.ProgressTranslatingFragment(fragment.Index, fragment.Total, left),
            now);
    }

    private bool SilenceEarnsALine(DateTimeOffset now) =>
        lastLineAt is { } last
            ? now - last >= LaterSilence
            : now - startedAt >= FirstSilence;

    private void Write(string line, DateTimeOffset now)
    {
        writer.WriteLine(line);
        lastLineAt = now;
    }

    /// <summary>
    /// Whole seconds, invariant. These lines are quoted verbatim out of redirected logs, and the
    /// notice beside them already prints whole seconds — two roundings would read as two clocks.
    /// </summary>
    private static int WholeSeconds(TimeSpan elapsed) =>
        (int)Math.Max(0, elapsed.TotalSeconds);
}
