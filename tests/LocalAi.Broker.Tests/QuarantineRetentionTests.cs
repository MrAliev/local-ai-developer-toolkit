using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Quarantined jobs hold full request bodies and used to be the one artifact no retention
/// bound covered (#204). Three bounds now apply — age, count, bytes — and nothing younger
/// than the quarantine grace is ever touched: a fresh entry exists to be inspected.
/// </summary>
public sealed class QuarantineRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-quarantine-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Nothing_inside_the_grace_is_ever_deleted()
    {
        var policy = Policy() with { QuarantineEntryLimit = 8, QuarantineBudgetBytes = 16L * 1024 * 1024 };
        var entries = Enumerable.Range(0, 20)
            .Select(index => new QuarantinedJobSnapshot(
                $"q{index}",
                Now - TimeSpan.FromHours(1),
                1024L * 1024 * 1024))
            .ToArray();

        Assert.Empty(QuarantineRetention.Plan(entries, policy, Now));
    }

    [Fact]
    public void Age_alone_dooms_an_entry_past_the_retention()
    {
        var doomed = QuarantineRetention.Plan(
            [
                new QuarantinedJobSnapshot("old", Now - TimeSpan.FromDays(15), 10),
                new QuarantinedJobSnapshot("young", Now - TimeSpan.FromDays(2), 10),
            ],
            Policy(),
            Now);

        Assert.Equal(["old"], doomed);
    }

    [Fact]
    public void The_entry_limit_dooms_the_oldest_expendable_entries_first()
    {
        var policy = Policy() with { QuarantineEntryLimit = 8 };
        var entries = Enumerable.Range(0, 10)
            .Select(index => new QuarantinedJobSnapshot(
                $"q{index}",
                Now - TimeSpan.FromDays(2) - TimeSpan.FromMinutes(index),
                10))
            .ToArray();

        var doomed = QuarantineRetention.Plan(entries, policy, Now);

        Assert.Equal(["q9", "q8"], doomed);
    }

    [Fact]
    public void The_byte_budget_dooms_the_oldest_until_the_rest_fit()
    {
        var policy = Policy() with { QuarantineBudgetBytes = 16L * 1024 * 1024 };
        var megabyte = 1024L * 1024;
        var doomed = QuarantineRetention.Plan(
            [
                new QuarantinedJobSnapshot("oldest", Now - TimeSpan.FromDays(3), 10 * megabyte),
                new QuarantinedJobSnapshot("middle", Now - TimeSpan.FromDays(2), 10 * megabyte),
                new QuarantinedJobSnapshot("newest", Now - TimeSpan.FromDays(1), 10 * megabyte),
            ],
            policy,
            Now);

        Assert.Equal(["oldest", "middle"], doomed);
    }

    /// <summary>
    /// The sweep end to end: real directories, a marker stamped in the past, small bounds.
    /// </summary>
    [Fact]
    public void The_archive_sweep_deletes_expired_quarantine_entries()
    {
        var quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        var expired = QuarantinedEntry(quarantine, "expired", DateTime.UtcNow.AddDays(-20));
        var fresh = QuarantinedEntry(quarantine, "fresh", DateTime.UtcNow);
        var queue = new DurableQueue(_root, retention: Policy());

        var result = queue.SweepArchive(force: true);

        Assert.Equal(1, result.QuarantineDeleted);
        Assert.False(Directory.Exists(expired));
        Assert.True(Directory.Exists(fresh));
        Assert.True(result.BytesReclaimed > 0);
    }

    [Fact]
    public void A_dry_run_reports_without_deleting()
    {
        var quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        var expired = QuarantinedEntry(quarantine, "expired", DateTime.UtcNow.AddDays(-20));
        var queue = new DurableQueue(_root, retention: Policy());

        var result = queue.SweepArchive(force: true, dryRun: true);

        Assert.Equal(1, result.QuarantineDeleted);
        Assert.True(Directory.Exists(expired));
    }

    private static string QuarantinedEntry(
        string quarantineRoot,
        string name,
        DateTime quarantinedAtUtc)
    {
        var directory = Path.Combine(quarantineRoot, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "request.json"), new string('x', 4096));
        var marker = Path.Combine(directory, DurableQueue.QuarantineMarkerFileName);
        File.WriteAllText(marker, quarantinedAtUtc.ToString("O"));
        File.SetLastWriteTimeUtc(marker, quarantinedAtUtc);
        return directory;
    }

    private static RuntimeRetentionPolicy Policy() => RuntimeRetentionPolicy.Default;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
