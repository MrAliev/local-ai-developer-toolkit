using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The loop that decides whether to look. What these pin is mostly the not-looking: a machine
/// that never opted in makes no request, and one that already asked today does not ask again
/// because something restarted.
/// </summary>
public sealed class UpdateCheckServiceTests : IDisposable
{
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-update-service-" + Guid.NewGuid().ToString("N"));

    public UpdateCheckServiceTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_machine_that_never_opted_in_makes_no_request()
    {
        var calls = 0;

        var checked_ = await Service(Noon, () => calls++)
            .RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(checked_);
        Assert.Equal(0, calls);
        Assert.Equal(UpdateCheckStatus.Unknown, State().Status);
    }

    [Fact]
    public async Task Switching_the_check_off_stops_it_at_the_next_pass()
    {
        Enable();
        var calls = 0;
        var service = Service(Noon, () => calls++);
        Assert.True(await service.RunOnceAsync(TestContext.Current.CancellationToken));

        Disable();
        var second = await Service(Noon.AddDays(2), () => calls++)
            .RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_permitted_check_records_what_it_found()
    {
        Enable();

        var checked_ = await Service(Noon, () => { }, Verified("0.1.51"))
            .RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(checked_);
        var state = State();
        Assert.Equal(UpdateCheckStatus.Verified, state.Status);
        Assert.Equal("0.1.51", state.LatestVersion);
        Assert.Equal(Noon, state.CheckedAtUtc);
    }

    /// <summary>
    /// The throttle survives a restart because it is on disk, not in memory: a machine that
    /// reboots every hour must not make a request every hour.
    /// </summary>
    [Fact]
    public async Task A_check_that_already_ran_today_is_not_repeated_by_a_restart()
    {
        Enable();
        var calls = 0;
        await Service(Noon, () => calls++, Verified("0.1.51"))
            .RunOnceAsync(TestContext.Current.CancellationToken);

        // A brand new service, as a restarted broker would build.
        var again = await Service(Noon.AddHours(1), () => calls++, Verified("0.1.51"))
            .RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(again);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Past_the_interval_it_asks_again()
    {
        Enable();
        var calls = 0;
        await Service(Noon, () => calls++, Verified("0.1.51"))
            .RunOnceAsync(TestContext.Current.CancellationToken);

        var again = await Service(Noon.AddHours(27), () => calls++, Verified("0.1.52"))
            .RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(again);
        Assert.Equal(2, calls);
        Assert.Equal("0.1.52", State().LatestVersion);
    }

    /// <summary>
    /// The check is the least important thing this process does. Whatever it throws, the
    /// broker carries on and the failure is recorded as "nothing to believe" — which also
    /// throttles, so a probe that throws every time is still asked only once per interval.
    /// </summary>
    [Fact]
    public async Task A_probe_that_throws_is_recorded_rather_than_raised()
    {
        Enable();

        var checked_ = await new UpdateCheckService(
                root,
                (_, _) => throw new InvalidOperationException("boom"),
                new FixedTime(Noon))
            .RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(checked_);
        Assert.Equal(UpdateCheckStatus.Unavailable, State().Status);
        Assert.Equal(Noon, State().CheckedAtUtc);
    }

    [Fact]
    public async Task Cancelling_the_broker_cancels_the_check()
    {
        Enable();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateCheckService(
                    root,
                    (_, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult(UpdateCheckState.Unknown);
                    },
                    new FixedTime(Noon))
                .RunOnceAsync(cancellation.Token));
    }

    [Fact]
    public async Task The_loop_ends_when_the_broker_does()
    {
        Enable();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var service = new UpdateCheckService(
            root,
            (now, _) =>
            {
                calls++;
                cancellation.Cancel();
                return Task.FromResult(Verified("0.1.51")(now));
            },
            new FixedTime(Noon));

        await service.RunAsync(cancellation.Token);

        Assert.Equal(1, calls);
    }

    private UpdateCheckState State() => new UpdateCheckStateStore(root).Read();

    private void Enable() =>
        new UpdateCheckPolicyStore(root).Write(
            UpdateCheckPolicy.Default with { Enabled = true });

    private void Disable() =>
        new UpdateCheckPolicyStore(root).Write(
            UpdateCheckPolicy.Default with { Enabled = false });

    private static Func<DateTimeOffset, UpdateCheckState> Verified(string version) =>
        now => new UpdateCheckState(
            1,
            UpdateCheckStatus.Verified,
            now,
            version,
            "https://example.invalid/releases/tag/v" + version);

    private UpdateCheckService Service(
        DateTimeOffset now,
        Action onCheck,
        Func<DateTimeOffset, UpdateCheckState>? result = null) =>
        new(
            root,
            (checkedAt, _) =>
            {
                onCheck();
                return Task.FromResult(
                    result?.Invoke(checkedAt) ??
                    new UpdateCheckState(1, UpdateCheckStatus.Unavailable, checkedAt, null, null));
            },
            new FixedTime(now));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
