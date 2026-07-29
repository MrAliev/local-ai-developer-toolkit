using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelAwareSchedulerTests
{
    private readonly ManualTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 29, 1, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Frozen_snapshot_uses_shortest_job_first_and_excludes_late_arrivals()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var first = Job(1, "model-a", seconds: 8);
        var second = Job(2, "model-a", seconds: 3);

        var start = scheduler.Decide([first, second], residentModel: "model-a");
        Assert.Equal(second.JobId, start.JobId);

        var late = Job(3, "model-a", seconds: 1);
        var next = scheduler.Decide([first, second, late], residentModel: "model-a");
        Assert.Equal(first.JobId, next.JobId);

        var recalculated = scheduler.Decide([late], residentModel: "model-a");
        Assert.Equal(late.JobId, recalculated.JobId);
    }

    [Fact]
    public void Resident_model_affinity_wins_before_shorter_cold_work()
    {
        var scheduler = new ModelAwareScheduler(_time);

        var decision = scheduler.Decide(
            [
                Job(1, "resident", seconds: 8),
                Job(2, "cold", seconds: 1)
            ],
            residentModel: "resident");

        Assert.Equal("resident", decision.Model);
        Assert.Equal(JobId(1), decision.JobId);
    }

    [Fact]
    public void Model_switch_opens_one_two_second_related_work_window()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var candidate = Job(1, "cold", seconds: 3);

        var waiting = scheduler.Decide([candidate], residentModel: "other");

        Assert.Null(waiting.JobId);
        Assert.Equal(_time.GetUtcNow().AddSeconds(2), waiting.WaitUntilUtc);

        _time.Advance(TimeSpan.FromSeconds(2));
        var ready = scheduler.Decide(
            [candidate, Job(2, "cold", seconds: 1)],
            residentModel: "other");
        Assert.Equal(JobId(2), ready.JobId);
    }

    [Fact]
    public void Fifteen_minute_age_forces_the_next_compatible_snapshot()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var starved = Job(
            1,
            "cold",
            seconds: 30,
            createdAtUtc: _time.GetUtcNow().AddMinutes(-15));
        var resident = Job(2, "resident", seconds: 1);

        var decision = scheduler.Decide(
            [resident, starved],
            residentModel: "resident");

        Assert.Equal("cold", decision.Model);
        Assert.Equal(starved.JobId, decision.JobId);
    }

    [Fact]
    public void Dependency_not_ready_is_not_scheduled()
    {
        var scheduler = new ModelAwareScheduler(_time);

        var decision = scheduler.Decide(
            [
                Job(1, "model", seconds: 1, dependencyReady: false),
                Job(2, "model", seconds: 2)
            ],
            residentModel: "model");

        Assert.Equal(JobId(2), decision.JobId);
    }

    [Fact]
    public void Maintenance_waits_until_no_inference_job_is_queued()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var maintenance = Job(1, "maintenance", seconds: 1) with
        {
            IsMaintenance = true
        };
        var inference = Job(2, "model", seconds: 20);

        var decision = scheduler.Decide(
            [maintenance, inference],
            residentModel: "model");

        Assert.Equal(inference.JobId, decision.JobId);
    }

    [Fact]
    public void Maintenance_waits_for_dependency_blocked_inference_work()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var maintenance = Job(1, "maintenance", seconds: 1) with
        {
            IsMaintenance = true
        };
        var blockedInference = Job(
            2,
            "model",
            seconds: 20,
            dependencyReady: false);

        var decision = scheduler.Decide(
            [maintenance, blockedInference],
            residentModel: "maintenance");

        Assert.Null(decision.JobId);
    }

    private ScheduledJobCandidate Job(
        long sequence,
        string model,
        int seconds,
        DateTimeOffset? createdAtUtc = null,
        bool dependencyReady = true) =>
        new(
            JobId(sequence),
            sequence,
            LocalJobPriority.Foreground,
            createdAtUtc ?? _time.GetUtcNow(),
            model,
            TimeSpan.FromSeconds(seconds),
            LocalDurationClass.Short,
            dependencyReady);

    private static Guid JobId(long value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        return new Guid(bytes);
    }
}
