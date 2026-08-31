using System.Text;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The record every surface answers from, and the rule deciding when it may be refreshed.
/// Both are deliberately free of any network: what these pin is arithmetic and file handling,
/// which is all a throttle is.
/// </summary>
public sealed class UpdateCheckStateTests : IDisposable
{
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-update-state-" + Guid.NewGuid().ToString("N"));

    public UpdateCheckStateTests() => Directory.CreateDirectory(root);

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
    public void A_machine_that_has_never_checked_knows_nothing()
    {
        var state = new UpdateCheckStateStore(root).Read();

        Assert.Equal(UpdateCheckStatus.Unknown, state.Status);
        Assert.Null(state.CheckedAtUtc);
        Assert.Null(state.LatestVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"SchemaVersion":7,"Status":"Verified","LatestVersion":"9.9.9"}""")]
    public void An_unreadable_record_is_unknown_rather_than_believed(string content)
    {
        File.WriteAllText(
            Path.Combine(root, UpdateCheckState.FileName),
            content,
            Encoding.UTF8);

        Assert.Equal(UpdateCheckStatus.Unknown, new UpdateCheckStateStore(root).Read().Status);
    }

    [Fact]
    public void What_a_check_learned_survives_a_round_trip()
    {
        var store = new UpdateCheckStateStore(root);
        var written = new UpdateCheckState(
            1,
            UpdateCheckStatus.Verified,
            Noon,
            "0.1.51",
            "https://github.com/MrAliev/local-ai-developer-toolkit/releases/tag/v0.1.51");

        store.Write(written);

        Assert.Equal(written, store.Read());
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    /// <summary>
    /// Ordering by version rather than by text: "0.1.9" sorts after "0.1.10" as a string, and
    /// a machine told it was ahead of a release it is behind would never be offered the fix.
    /// </summary>
    [Theory]
    [InlineData("0.1.51", "0.1.50", true)]
    [InlineData("0.1.10", "0.1.9", true)]
    [InlineData("0.1.50", "0.1.50", false)]
    [InlineData("0.1.49", "0.1.50", false)]
    [InlineData("v0.1.51", "0.1.50", true)]
    [InlineData("0.1.51", "v0.1.50", true)]
    public void A_newer_release_is_recognised_as_a_version_not_as_text(
        string latest,
        string installed,
        bool expected)
    {
        var state = new UpdateCheckState(1, UpdateCheckStatus.Verified, Noon, latest, null);

        Assert.Equal(expected, state.IsNewerThan(installed));
    }

    [Theory]
    [InlineData("nightly-build")]
    [InlineData("")]
    [InlineData(null)]
    public void A_version_nobody_can_order_is_not_an_update(string? latest)
    {
        var state = new UpdateCheckState(1, UpdateCheckStatus.Verified, Noon, latest, null);

        Assert.False(state.IsNewerThan("0.1.50"));
    }

    [Fact]
    public void An_unverified_answer_is_never_an_update()
    {
        var state = new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Noon, "9.9.9", null);

        Assert.False(state.IsNewerThan("0.1.50"));
    }

    [Fact]
    public void Nothing_is_due_while_the_check_is_switched_off()
    {
        Assert.False(UpdateCheckSchedule.IsDue(
            UpdateCheckPolicy.Default,
            UpdateCheckState.Unknown,
            Noon,
            root));
    }

    [Fact]
    public void The_first_check_after_switching_on_is_due_immediately()
    {
        Assert.True(UpdateCheckSchedule.IsDue(
            UpdateCheckPolicy.Default with { Enabled = true },
            UpdateCheckState.Unknown,
            Noon,
            root));
    }

    [Fact]
    public void A_check_that_just_ran_is_not_due_again()
    {
        var policy = UpdateCheckPolicy.Default with { Enabled = true, IntervalHours = 24 };
        var state = new UpdateCheckState(1, UpdateCheckStatus.Verified, Noon, "0.1.50", null);

        Assert.False(UpdateCheckSchedule.IsDue(policy, state, Noon.AddHours(23), root));
        // Past the interval and its jitter, which is at most a tenth of it.
        Assert.True(UpdateCheckSchedule.IsDue(policy, state, Noon.AddHours(27), root));
    }

    /// <summary>
    /// A failed check throttles exactly like a successful one: an unreachable network must not
    /// turn into a request every time anything looks at the state file.
    /// </summary>
    [Fact]
    public void A_check_that_produced_nothing_still_waits_its_turn()
    {
        var policy = UpdateCheckPolicy.Default with { Enabled = true };
        var state = new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Noon, null, null);

        Assert.False(UpdateCheckSchedule.IsDue(policy, state, Noon.AddMinutes(1), root));
    }

    /// <summary>
    /// Machines installed from one image would otherwise all check at the same instant after a
    /// restart. The offset is derived from the machine rather than drawn at random, so it stays
    /// the same across restarts instead of walking around the clock.
    /// </summary>
    [Fact]
    public void Two_machines_do_not_check_at_the_same_moment()
    {
        var policy = UpdateCheckPolicy.Default with { Enabled = true };

        var first = UpdateCheckSchedule.NextDue(policy, Noon, @"C:\Users\one\AppData\Local\LocalAi");
        var second = UpdateCheckSchedule.NextDue(policy, Noon, @"C:\Users\two\AppData\Local\LocalAi");

        Assert.NotEqual(first, second);
        Assert.Equal(
            first,
            UpdateCheckSchedule.NextDue(policy, Noon, @"C:\Users\one\AppData\Local\LocalAi"));
    }

    [Fact]
    public void The_drift_stays_inside_a_tenth_of_the_interval()
    {
        var policy = UpdateCheckPolicy.Default with { Enabled = true, IntervalHours = 24 };
        var interval = TimeSpan.FromHours(24);

        foreach (var seed in new[] { "a", "b", "c", "d", "e", "f", "g", "h" })
        {
            var due = UpdateCheckSchedule.NextDue(policy, Noon, seed);

            Assert.InRange(
                due,
                Noon + interval,
                Noon + interval + (interval * UpdateCheckSchedule.JitterFraction));
        }
    }
}
