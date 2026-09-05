using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Broker;

/// <summary>
/// Where the broker's own failures are kept, so that a stall can be explained after it ends.
///
/// It reported them to standard error, and nothing captured that: the broker runs detached and
/// there was no log anywhere under the runtime root. When a queue stopped for two hours (#335),
/// the line naming the exception went to a stream with no reader, and nothing on disk could say
/// afterwards what had happened. Every other durable thing this runtime produces — the queue, the
/// archive, the quarantine, the telemetry — is written down and bounded; this was the exception.
///
/// One JSON object per line, because the reader is as likely to be a script answering "what
/// happened at 09:21" as a person scrolling.
/// </summary>
public sealed class BrokerDiagnosticLog
{
    /// <summary>
    /// Two files, and a size rather than an age. A failure that repeats on every scheduling turn
    /// writes thousands of identical lines in a minute, so an age-based bound would keep all of
    /// them; a size-based one keeps the recent ones and, through the single rotation, the first —
    /// which is usually the interesting one, the failure that started the run.
    /// </summary>
    private const long DefaultMaximumBytes = 1024 * 1024;

    private readonly string _path;
    private readonly string _previousPath;
    private readonly long _maximumBytes;
    private readonly Lock _gate = new();

    public BrokerDiagnosticLog(string runtimeRoot, long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _path = Path.Combine(runtimeRoot, "diagnostics.jsonl");
        _previousPath = Path.Combine(runtimeRoot, "diagnostics.1.jsonl");
        _maximumBytes = maximumBytes;
    }

    /// <summary>
    /// Records one failure, and never throws.
    ///
    /// This is called from the broker's own failure paths. An exception here would replace the
    /// failure being reported with one about reporting it, which is how a diagnostic makes an
    /// incident harder to read rather than easier.
    /// </summary>
    public void Write(string operation, string failure, Guid jobId, string? reason = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new Entry(
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                operation,
                failure,
                jobId == Guid.Empty ? null : jobId.ToString("N"),
                string.IsNullOrWhiteSpace(reason) ? null : reason));

            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfFull(Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or JsonException)
        {
        }
    }

    private void RotateIfFull(int incoming)
    {
        var current = new FileInfo(_path);
        if (!current.Exists || current.Length + incoming <= _maximumBytes)
        {
            return;
        }

        // The previous rotation is overwritten rather than kept as a third file: two is what makes
        // the first failure of a run survive its hundredth, and more would be a retention policy
        // nobody asked for.
        File.Move(_path, _previousPath, overwrite: true);
    }

    /// <summary>
    /// Field names are explicit, as they are on every other shape this product puts on disk or on
    /// a wire: what a later reader parses should be visible in the source rather than produced by
    /// a policy set somewhere else.
    /// </summary>
    private sealed record Entry(
        [property: JsonPropertyName("atUtc"), JsonPropertyOrder(0)]
        string AtUtc,
        [property: JsonPropertyName("operation"), JsonPropertyOrder(1)]
        string Operation,
        [property: JsonPropertyName("failure"), JsonPropertyOrder(2)]
        string Failure,
        /// Absent for a failure that happened before any job was leased — which is what a
        /// scheduling failure is, and the one this log was written for.
        [property: JsonPropertyName("jobId"), JsonPropertyOrder(3),
                   JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? JobId,
        /// What was reported with the failure, whole: the type left of it says what kind of
        /// failure it was, and this says what it said. Last, and absent when there is nothing,
        /// so the short fields a reader scans stay where they were.
        [property: JsonPropertyName("reason"), JsonPropertyOrder(4),
                   JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Reason = null);
}
