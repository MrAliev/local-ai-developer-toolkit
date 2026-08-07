using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ArchiveRetentionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly RuntimeRetentionPolicy Policy = RuntimeRetentionPolicy.Default;

    [Fact]
    public void Nothing_inside_the_response_grace_is_ever_touched()
    {
        // The grace is the one bound here that protects a running client rather than the disk:
        // a job that just turned terminal still has a caller polling for its body. Five thousand
        // entries is well past the entry limit and 312 GB is well past the byte budget, and
        // neither bound may reach inside the grace to satisfy itself.
        var entries = Enumerable.Range(0, 5000)
            .Select(index => new ArchivedJobSnapshot(
                $"job-{index}",
                Now - TimeSpan.FromMilliseconds(index * 100),
                ResponseBytes: 64L * 1024 * 1024))
            .ToArray();

        var plan = ArchiveRetention.Plan(entries, Policy, Now);

        Assert.Empty(plan.DirectoriesToDelete);
        Assert.Empty(plan.ResponsesToDrop);
    }

    [Fact]
    public void Expired_entries_are_deleted_whole()
    {
        var entries = new[]
        {
            Entry("old", TimeSpan.FromDays(30), 1024),
            Entry("recent", TimeSpan.FromHours(2), 1024),
        };

        var plan = ArchiveRetention.Plan(entries, Policy, Now);

        Assert.Equal(["old"], plan.DirectoriesToDelete);
        Assert.DoesNotContain("old", plan.ResponsesToDrop);
    }

    [Fact]
    public void Bodies_older_than_the_response_retention_are_dropped_but_the_job_stays()
    {
        var entries = new[] { Entry("aged", TimeSpan.FromHours(3), 1024) };

        var plan = ArchiveRetention.Plan(entries, Policy, Now);

        Assert.Empty(plan.DirectoriesToDelete);
        Assert.Equal(["aged"], plan.ResponsesToDrop);
    }

    [Fact]
    public void The_byte_budget_drops_the_oldest_bodies_first_and_stops_at_the_budget()
    {
        // Two hundred 8 MB bodies is 1600 MB against a 512 MB budget, all of them younger than
        // the hour-long response retention. Without a budget an index run fills the disk long
        // before the age bound ever applies.
        var entries = Enumerable.Range(0, 200)
            .Select(index => Entry(
                $"job-{index:D3}",
                TimeSpan.FromMinutes(11 + index),
                8L * 1024 * 1024))
            .ToArray();

        var plan = ArchiveRetention.Plan(entries, Policy, Now);

        Assert.Empty(plan.DirectoriesToDelete);
        var retained = entries
            .Where(entry => !plan.ResponsesToDrop.Contains(entry.Directory))
            .Sum(entry => entry.ResponseBytes ?? 0);
        Assert.True(retained <= Policy.ResponseBudgetBytes);
        // Oldest first: the newest entry keeps its body.
        Assert.DoesNotContain("job-000", plan.ResponsesToDrop);
        Assert.Contains("job-199", plan.ResponsesToDrop);
    }

    [Fact]
    public void The_entry_limit_removes_the_oldest_beyond_it()
    {
        var policy = Policy with { ArchiveEntryLimit = 32, ArchiveRetentionDays = 3650 };
        var entries = Enumerable.Range(0, 40)
            .Select(index => Entry($"job-{index:D3}", TimeSpan.FromHours(index + 1), null))
            .ToArray();

        var plan = ArchiveRetention.Plan(entries, policy, Now);

        Assert.Equal(8, plan.DirectoriesToDelete.Count);
        Assert.Contains("job-039", plan.DirectoriesToDelete);
        Assert.DoesNotContain("job-000", plan.DirectoriesToDelete);
    }

    [Fact]
    public void A_sweep_never_exceeds_its_action_budget()
    {
        var policy = Policy with { MaximumActionsPerSweep = 10 };
        var entries = Enumerable.Range(0, 500)
            .Select(index => Entry($"job-{index:D3}", TimeSpan.FromDays(30), 1024))
            .ToArray();

        var plan = ArchiveRetention.Plan(entries, policy, Now);

        Assert.Equal(10, plan.ActionCount);
    }

    [Fact]
    public void A_hand_edited_policy_cannot_remove_the_grace()
    {
        var reckless = Policy with
        {
            ResponseGraceMinutes = -1,
            ResponseRetentionMinutes = 0,
            ArchiveRetentionDays = 0,
            ArchiveEntryLimit = 0,
        };
        var entries = new[] { Entry("fresh", TimeSpan.FromSeconds(1), 1024) };

        var plan = ArchiveRetention.Plan(entries, reckless, Now);

        Assert.Equal(0, plan.ActionCount);
    }

    [Fact]
    public async Task The_queue_reclaims_archived_bodies_and_keeps_the_audit_trail()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(Now);
        var queue = new DurableQueue(root.Path, clock);
        var archived = await ArchiveAsync(queue, root, "sweep-body", new string('x', 4096));
        Assert.True(File.Exists(Path.Combine(archived.Directory, "response.json")));

        clock.Advance(TimeSpan.FromHours(4));
        var result = queue.SweepArchive(force: true);

        Assert.Equal(1, result.ResponsesDropped);
        Assert.Equal(0, result.JobsDeleted);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(File.Exists(Path.Combine(archived.Directory, "response.json")));
        // The job itself is the record of what ran; only the payload no reader exists for goes.
        Assert.True(File.Exists(Path.Combine(archived.Directory, "state.json")));
        Assert.True(File.Exists(Path.Combine(archived.Directory, "request.json")));
        Assert.NotNull(await queue.GetDiagnosticAsync(archived.JobId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_response_a_client_could_still_be_waiting_for_survives_a_forced_sweep()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(Now);
        var queue = new DurableQueue(root.Path, clock);
        var archived = await ArchiveAsync(queue, root, "sweep-fresh", "value");

        clock.Advance(TimeSpan.FromMinutes(1));
        var result = queue.SweepArchive(force: true);

        Assert.Equal(RetentionSweepResult.Empty, result);
        Assert.NotNull(await queue.ReadResponseAsync(archived.JobId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_expired_archived_job_leaves_entirely()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(Now);
        var queue = new DurableQueue(root.Path, clock);
        var archived = await ArchiveAsync(queue, root, "sweep-whole", "value");

        clock.Advance(TimeSpan.FromDays(30));
        var result = queue.SweepArchive(force: true);

        Assert.Equal(1, result.JobsDeleted);
        Assert.False(Directory.Exists(archived.Directory));
        Assert.Null(await queue.GetDiagnosticAsync(archived.JobId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_dry_run_reports_without_removing_anything()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(Now);
        var queue = new DurableQueue(root.Path, clock);
        var archived = await ArchiveAsync(queue, root, "sweep-dry", "value");

        clock.Advance(TimeSpan.FromDays(30));
        var result = queue.SweepArchive(force: true, dryRun: true);

        Assert.Equal(1, result.JobsDeleted);
        Assert.True(Directory.Exists(archived.Directory));
    }

    [Fact]
    public async Task Corrupt_archive_history_does_not_fail_an_active_operation()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(Now);
        var corrupt = Path.Combine(root.Path, "archive", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corrupt);
        var statePath = Path.Combine(corrupt, "state.json");
        await File.WriteAllTextAsync(statePath, "{corrupt", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(statePath, Now.UtcDateTime);
        var queue = new DurableQueue(root.Path, clock);

        clock.Advance(TimeSpan.FromDays(30));
        var result = queue.SweepArchive(force: true);
        var enqueue = await queue.EnqueueAsync(Request("after-corrupt"), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.JobsDeleted);
        Assert.False(Directory.Exists(corrupt));
        Assert.NotEqual(Guid.Empty, enqueue.JobId);
    }

    [Fact]
    public async Task An_unreadable_entry_inside_the_retention_window_is_left_alone()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(Now);
        var corrupt = Path.Combine(root.Path, "archive", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corrupt);
        var statePath = Path.Combine(corrupt, "state.json");
        await File.WriteAllTextAsync(statePath, "{corrupt", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(statePath, Now.UtcDateTime);
        var queue = new DurableQueue(root.Path, clock);

        clock.Advance(TimeSpan.FromHours(3));
        var result = queue.SweepArchive(force: true);

        Assert.Equal(RetentionSweepResult.Empty, result);
        Assert.True(Directory.Exists(corrupt));
    }

    [Fact]
    public async Task An_unforced_sweep_stands_down_when_another_process_swept_recently()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new ManualTimeProvider(Now);
        var queue = new DurableQueue(root.Path, clock);
        var archived = await ArchiveAsync(queue, root, "throttle", "value");
        clock.Advance(TimeSpan.FromDays(30));
        // Stands in for a sibling process that swept ten seconds ago. Without the on-disk
        // marker every short-lived CLI invocation would walk the whole archive on startup.
        await File.WriteAllTextAsync(
            Path.Combine(root.Path, "archive-sweep.json"),
            $$"""{"SchemaVersion":1,"SweptAtUtc":"{{(clock.GetUtcNow() - TimeSpan.FromSeconds(10)):O}}"}""",
            TestContext.Current.CancellationToken);

        var throttled = new DurableQueue(root.Path, clock).SweepArchive();

        Assert.Equal(RetentionSweepResult.Empty, throttled);
        Assert.True(Directory.Exists(archived.Directory));
        Assert.Equal(1, new DurableQueue(root.Path, clock).SweepArchive(force: true).JobsDeleted);
    }

    /// <summary>
    /// Completes a job and puts its archived files at the clock's current instant, so advancing
    /// the clock ages the recorded state and the file timestamps by the same amount. The sweep
    /// selects candidates by file stat and confirms them against the recorded state; a test that
    /// moved only one of the two would exercise neither path honestly.
    /// </summary>
    private static async Task<(Guid JobId, string Directory)> ArchiveAsync(
        DurableQueue queue,
        TemporaryRuntimeRoot root,
        string key,
        string body)
    {
        var enqueue = await queue.EnqueueAsync(Request(key), TestContext.Current.CancellationToken);
        var lease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker", TestContext.Current.CancellationToken));
        await queue.CompleteAsync(
            enqueue.JobId,
            "worker",
            lease.LeaseId,
            JsonSerializer.SerializeToElement(body),
            TestContext.Current.CancellationToken);
        var directory = Path.Combine(root.Path, "archive", enqueue.JobId.ToString("N"));
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.SetLastWriteTimeUtc(file, Now.UtcDateTime);
        }

        return (enqueue.JobId, directory);
    }

    private static ArchivedJobSnapshot Entry(string name, TimeSpan age, long? bytes) =>
        new(name, Now - age, bytes);

    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateEmbed(
            key,
            LocalJobPriority.Foreground,
            "test-model",
            ["input"]);
}
