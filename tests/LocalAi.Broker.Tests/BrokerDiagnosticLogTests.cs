using System.Text.Json;
using LocalAi.Broker;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Where the broker's own failures go.
///
/// It reported them to its standard error, and nothing captured that: the broker runs detached,
/// and there is no log under the runtime root. So when a queue stalled for two hours (#335), the
/// one line that would have named the exception was written to a stream with no reader, and the
/// cause could not be established afterwards from anything on disk.
/// </summary>
public sealed class BrokerDiagnosticLogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-diagnostics-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// One line per entry, machine-readable, because the reader is as likely to be a script
    /// answering "what happened at 09:21" as a person.
    /// </summary>
    [Fact]
    public void What_failed_is_written_where_a_later_reader_can_find_it()
    {
        var log = new BrokerDiagnosticLog(_root);

        log.Write("schedule", "ArgumentOutOfRangeException", Guid.Empty);

        var line = File.ReadAllLines(Path.Combine(_root, "diagnostics.jsonl")).Single();
        using var entry = JsonDocument.Parse(line);
        Assert.Equal("schedule", entry.RootElement.GetProperty("operation").GetString());
        Assert.Equal(
            "ArgumentOutOfRangeException",
            entry.RootElement.GetProperty("failure").GetString());
        Assert.True(entry.RootElement.TryGetProperty("atUtc", out _));
    }

    /// <summary>
    /// Everything the runtime writes is bounded — the queue, the archive, the quarantine, the
    /// telemetry — and a log that grows without one is a disk filling up on the machine of
    /// somebody who never asked for it. A failure that repeats every scheduling turn is exactly
    /// the shape that would do it.
    /// </summary>
    [Fact]
    public void It_bounds_itself_rather_than_growing_with_a_repeating_failure()
    {
        var log = new BrokerDiagnosticLog(_root, maximumBytes: 2048);

        for (var attempt = 0; attempt < 500; attempt++)
        {
            log.Write("schedule", "ArgumentOutOfRangeException", Guid.NewGuid());
        }

        var current = new FileInfo(Path.Combine(_root, "diagnostics.jsonl"));
        Assert.True(
            current.Length <= 2048,
            $"the live log is {current.Length} bytes, past its own limit");
    }

    /// <summary>
    /// The previous file survives one rotation, because the interesting entry is usually the
    /// first one — the failure that started a run of them — and a single file would have thrown
    /// it away by the time anybody looked.
    /// </summary>
    [Fact]
    public void One_rotation_is_kept_so_the_first_failure_survives_the_hundredth()
    {
        var log = new BrokerDiagnosticLog(_root, maximumBytes: 512);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            log.Write("schedule", "ArgumentOutOfRangeException", Guid.NewGuid());
        }

        Assert.True(File.Exists(Path.Combine(_root, "diagnostics.1.jsonl")));
    }

    /// <summary>
    /// A diagnostic that throws while diagnosing is worse than one that says nothing: this is
    /// called from the broker's own failure paths, where an exception would replace the failure
    /// being reported with one about reporting.
    /// </summary>
    [Fact]
    public void A_log_that_cannot_be_written_never_breaks_the_thing_it_reports()
    {
        var log = new BrokerDiagnosticLog(Path.Combine(_root, "\0invalid"));

        var thrown = Record.Exception((Action)(
            () => log.Write("schedule", "ArgumentOutOfRangeException", Guid.Empty)));

        Assert.Null(thrown);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
