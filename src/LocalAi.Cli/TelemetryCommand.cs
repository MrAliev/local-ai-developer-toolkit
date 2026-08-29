using LocalAi.Broker;
using LocalAi.Contracts;
using LocalLm.Core;
using System.Globalization;
using System.Text;

namespace LocalAi.Cli;

public sealed record LatencySummary(TimeSpan Median, TimeSpan P90, TimeSpan Longest);

public sealed record TelemetryOutcomeCount(ModelExecutionOutcome Outcome, int Jobs);

public sealed record TelemetryBreakdown(
    string Name,
    int Jobs,
    int Succeeded,
    int Cold,
    int Fallback,
    TimeSpan MedianExecution,
    long NetTokensSaved);

public sealed record TelemetrySummary(
    int Jobs,
    int Unreadable,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest,
    IReadOnlyList<TelemetryOutcomeCount> Outcomes,
    int Cold,
    int Fallback,
    LatencySummary Queue,
    LatencySummary Execution,
    long GrossTokensSaved,
    long VerificationTokens,
    long NetTokensSaved,
    IReadOnlyList<TelemetryBreakdown> ByModel,
    IReadOnlyList<TelemetryBreakdown> ByProfile);

/// <summary>
/// Reports what the broker has already been measuring.
///
/// Every delegated job leaves a record under the runtime root and they are kept for a month, but
/// until now the only thing that could read any of them was the experiment report, which covers
/// the handful of jobs belonging to a running model experiment. The per-job records — every
/// routing decision, every cold load, every fallback, every estimate of what a job saved — were
/// written, retained, pruned on schedule, and never once read. Measuring something and never
/// looking at it is worse than not measuring it: it costs disk and buys the impression of
/// evidence.
///
/// This is a separate command rather than a part of <see cref="DoctorCommand"/> on purpose. The
/// doctor answers whether an installation is sound and says so in its exit code; this answers
/// what the installation has been doing, and there is no state of the world in which the answer
/// is a failure. Folding a report with no failure mode into a command whose exit code means
/// "something is broken" would either make the code meaningless or make a quiet month look like
/// a fault.
///
/// Every token figure is a range. The estimator works from characters, there is no live token
/// counter anywhere in this system, and a sum of a hundred thousand estimates is not more exact
/// than the estimates it is made of.
/// </summary>
public static class TelemetryCommand
{
    public static async Task<int> ExecuteAsync(
        string runtimeRoot,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(output);
        var read = await new ModelTelemetryStore(runtimeRoot)
            .ReadForReportAsync(cancellationToken);
        output.Write(Render(Summarize(read), Path.GetFullPath(runtimeRoot)));
        return 0;
    }

    public static TelemetrySummary Summarize(ModelTelemetryReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var records = read.Records;
        return new TelemetrySummary(
            records.Count,
            read.Unreadable,
            records.Count == 0 ? null : records.Min(record => record.RecordedAtUtc),
            records.Count == 0 ? null : records.Max(record => record.RecordedAtUtc),
            records
                .GroupBy(record => record.Outcome)
                .Select(group => new TelemetryOutcomeCount(group.Key, group.Count()))
                .OrderByDescending(outcome => outcome.Jobs)
                .ThenBy(outcome => outcome.Outcome.ToString(), StringComparer.Ordinal)
                .ToArray(),
            records.Count(record => record.WasCold),
            records.Count(record => record.UsedFallback),
            Latency(records.Select(record => record.QueueDuration)),
            Latency(records.Select(record => record.ExecutionDuration)),
            records.Sum(record => record.EstimatedGrossCloudTokensSaved),
            records.Sum(record => record.EstimatedVerificationTokens),
            records.Sum(record => record.EstimatedNetCloudTokensSaved),
            Breakdown(records, record => record.Model),
            Breakdown(records, record => record.TaskProfile.ToString()));
    }

    public static string Render(TelemetrySummary summary, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var text = new StringBuilder();
        if (summary.Jobs == 0)
        {
            // Not a failure and not an error: a runtime that has never been asked to run anything
            // locally has nothing to report, and so does one whose records have all aged out.
            text.AppendLine(
                $"No job telemetry under {runtimeRoot}. Nothing has been delegated here, or " +
                "everything recorded has passed its retention.");
            AppendUnreadable(text, summary);
            return text.ToString();
        }

        text.AppendLine(
            $"{summary.Jobs} job(s) recorded between " +
            $"{Moment(summary.Earliest)} and {Moment(summary.Latest)}.");
        AppendUnreadable(text, summary);
        text.AppendLine();
        text.AppendLine(
            "outcome     " + string.Join(
                ", ",
                summary.Outcomes.Select(outcome =>
                    $"{outcome.Outcome} {outcome.Jobs} ({Percent(outcome.Jobs, summary.Jobs)})")));
        text.AppendLine(
            $"loading     {summary.Cold} cold ({Percent(summary.Cold, summary.Jobs)}), " +
            $"{summary.Jobs - summary.Cold} warm " +
            $"({Percent(summary.Jobs - summary.Cold, summary.Jobs)})");
        text.AppendLine(
            $"fallback    {summary.Fallback} " +
            $"({Percent(summary.Fallback, summary.Jobs)})");
        foreach (var line in FailureOutliers(summary.ByModel))
        {
            text.AppendLine(line);
        }
        text.AppendLine($"queue       {Latencies(summary.Queue)}");
        text.AppendLine($"execution   {Latencies(summary.Execution)}");
        text.AppendLine(
            $"saved       {TokenEstimator.DescribeRange(summary.NetTokensSaved)} cloud tokens " +
            $"net, of {TokenEstimator.DescribeRange(summary.GrossTokensSaved)} gross " +
            $"({TokenEstimator.DescribeRange(summary.VerificationTokens)} spent verifying)");

        AppendBreakdown(text, "by model", summary.ByModel);
        AppendBreakdown(text, "by task profile", summary.ByProfile);
        return text.ToString();
    }

    /// <summary>
    /// One plain line for a model that owns the failures. The pattern this surfaces stayed
    /// invisible for a month on the reference machine: one model failed half of its jobs —
    /// every technical failure in the system — while the profile view showed acceptable
    /// success rates, because the fallback quietly absorbed each miss. The numbers were all
    /// printed; seeing the pattern required cross-reading two tables. A model is named here
    /// when it has enough jobs to mean something, fails at least a quarter of them, and
    /// carries at least half of all recorded failures.
    /// </summary>
    public static IReadOnlyList<string> FailureOutliers(
        IReadOnlyList<TelemetryBreakdown> byModel)
    {
        ArgumentNullException.ThrowIfNull(byModel);
        var totalFailures = byModel.Sum(row => row.Jobs - row.Succeeded);
        if (totalFailures == 0)
        {
            return [];
        }

        return byModel
            .Where(row => row.Jobs >= 10)
            .Select(row => (Row: row, Failures: row.Jobs - row.Succeeded))
            .Where(entry =>
                entry.Failures * 4 >= entry.Row.Jobs &&
                entry.Failures * 2 >= totalFailures)
            .OrderByDescending(entry => entry.Failures)
            .Select(entry =>
                $"attention   {entry.Row.Name} fails " +
                $"{Percent(entry.Failures, entry.Row.Jobs)} of its jobs — " +
                $"{entry.Failures} of the {totalFailures} failure(s) recorded; " +
                "see the by-model table")
            .ToArray();
    }

    private static void AppendUnreadable(StringBuilder text, TelemetrySummary summary)
    {
        if (summary.Unreadable > 0)
        {
            text.AppendLine(
                $"{summary.Unreadable} record(s) could not be read and are not counted in " +
                "anything below.");
        }
    }

    private static void AppendBreakdown(
        StringBuilder text,
        string title,
        IReadOnlyList<TelemetryBreakdown> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine(title);
        var width = rows.Max(row => row.Name.Length);
        foreach (var row in rows)
        {
            text.AppendLine(
                $"  {row.Name.PadRight(width)}  {row.Jobs,6} job(s)  " +
                $"{Percent(row.Succeeded, row.Jobs),4} ok  " +
                $"{Percent(row.Cold, row.Jobs),4} cold  " +
                $"{Percent(row.Fallback, row.Jobs),4} fallback  " +
                $"median {Duration(row.MedianExecution),7}  " +
                $"{TokenEstimator.DescribeRange(row.NetTokensSaved)}");
        }
    }

    private static IReadOnlyList<TelemetryBreakdown> Breakdown(
        IReadOnlyList<ModelTelemetryRecord> records,
        Func<ModelTelemetryRecord, string> key) =>
        records
            .GroupBy(key, StringComparer.Ordinal)
            .Select(group => new TelemetryBreakdown(
                group.Key,
                group.Count(),
                group.Count(record => record.Outcome == ModelExecutionOutcome.Success),
                group.Count(record => record.WasCold),
                group.Count(record => record.UsedFallback),
                Latency(group.Select(record => record.ExecutionDuration)).Median,
                group.Sum(record => record.EstimatedNetCloudTokensSaved)))
            .OrderByDescending(row => row.Jobs)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();

    private static LatencySummary Latency(IEnumerable<TimeSpan> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0
            ? new LatencySummary(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
            : new LatencySummary(
                Percentile(sorted, 0.5),
                Percentile(sorted, 0.9),
                sorted[^1]);
    }

    /// <summary>
    /// Nearest-rank, so every figure printed is a duration some job actually took. An
    /// interpolated median is a number that never happened, which is a strange thing to hand
    /// someone who is about to go looking for the job that took it.
    /// </summary>
    private static TimeSpan Percentile(TimeSpan[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static string Latencies(LatencySummary latency) =>
        $"median {Duration(latency.Median)}, p90 {Duration(latency.P90)}, " +
        $"longest {Duration(latency.Longest)}";

    internal static string Duration(TimeSpan value) => value.TotalMilliseconds switch
    {
        < 1000 => $"{value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms",
        < 60_000 => $"{value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s",
        _ => $"{(int)value.TotalMinutes}m{value.Seconds:D2}s",
    };

    private static string Percent(int part, int whole) =>
        whole == 0
            ? "0%"
            : $"{(100.0 * part / whole).ToString("F0", CultureInfo.InvariantCulture)}%";

    private static string Moment(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
}
