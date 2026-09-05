using LocalAi.Broker;

namespace LocalAi.Broker.Tests;

/// <summary>
/// What the backend says about a pull, turned into what a reader can use.
///
/// The backend counts per layer and restarts at zero for each one, so a line straight from it
/// would read as a failure and a retry six times in one download. This sums them, names the
/// phase in words the console owns rather than the backend's, and refuses to publish more often
/// than a reader could act on.
///
/// The clock is injected: nothing here waits for a duration.
/// </summary>
public sealed class ModelPullTrackerTests
{
    private DateTimeOffset now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private ModelPullTracker Tracker() => new(() => now);

    private void After(double seconds) => now = now.AddSeconds(seconds);

    /// <summary>The manifest fetch, before any layer size is known.</summary>
    [Fact]
    public void The_manifest_fetch_is_its_own_phase()
    {
        var position = Tracker().Accept(new ModelPullProgress("pulling manifest", null, 0, 0));

        Assert.Equal("preparing", position!.Phase, StringComparer.Ordinal);
        Assert.Equal(0, position.Total);
    }

    /// <summary>
    /// Layers are summed, so both figures only ever increase. The denominator grows as digests
    /// appear — which is why this reports two figures and never a percent: a percent against a
    /// growing denominator goes backwards, and there is no honest way to repair that.
    /// </summary>
    [Fact]
    public void Layers_are_summed_rather_than_reported_one_at_a_time()
    {
        var tracker = Tracker();

        tracker.Accept(new ModelPullProgress("pulling", "sha256:aaa", 100, 400));
        After(3);
        var position = tracker.Accept(new ModelPullProgress("pulling", "sha256:bbb", 200, 600));

        Assert.Equal("downloading", position!.Phase, StringComparer.Ordinal);
        Assert.Equal(300, position.Completed);
        Assert.Equal(1000, position.Total);
    }

    /// <summary>
    /// A layer that reports again replaces its own figure rather than adding to it. Without that
    /// the total would climb past the size of the download and stop being a size at all.
    /// </summary>
    [Fact]
    public void A_layer_reporting_again_replaces_what_it_said_before()
    {
        var tracker = Tracker();

        tracker.Accept(new ModelPullProgress("pulling", "sha256:aaa", 100, 400));
        After(3);
        var position = tracker.Accept(new ModelPullProgress("pulling", "sha256:aaa", 300, 400));

        Assert.Equal(300, position!.Completed);
        Assert.Equal(400, position.Total);
    }

    /// <summary>
    /// Bytes arrive many times a second and every publication is a file written next to the job.
    /// The reader cannot act on more than one every few seconds, so neither can the file.
    /// </summary>
    [Fact]
    public void Bytes_arriving_faster_than_a_reader_can_use_them_are_not_published()
    {
        var tracker = Tracker();
        tracker.Accept(new ModelPullProgress("pulling", "sha256:aaa", 100, 400));

        After(1);
        Assert.Null(tracker.Accept(new ModelPullProgress("pulling", "sha256:aaa", 150, 400)));

        After(1.1);
        Assert.NotNull(tracker.Accept(new ModelPullProgress("pulling", "sha256:aaa", 200, 400)));
    }

    /// <summary>
    /// A phase change is not a byte update: it is the answer to a different question, and holding
    /// it back for a throttle would leave the reader watching a download line while the run has
    /// moved on to hashing the file.
    /// </summary>
    [Fact]
    public void A_change_of_phase_is_published_at_once()
    {
        var tracker = Tracker();
        tracker.Accept(new ModelPullProgress("pulling", "sha256:aaa", 100, 400));

        After(0.2);
        var position = tracker.Accept(
            new ModelPullProgress("verifying sha256 digest", null, 0, 0));

        Assert.Equal("verifying", position!.Phase, StringComparer.Ordinal);
    }

    /// <summary>
    /// The backend's vocabulary is not ours to freeze. An unrecognised status is carried through
    /// as what it is — the backend's own word — rather than dressed as one of our phases, because
    /// mapping it to the nearest phase would be inventing a claim about what is happening.
    /// </summary>
    [Fact]
    public void An_unrecognised_status_is_carried_as_the_backend_word_it_is()
    {
        var position = Tracker().Accept(
            new ModelPullProgress("pulling fs layer", null, 0, 0));

        Assert.Equal("other", position!.Phase, StringComparer.Ordinal);
        Assert.Equal("pulling fs layer", position.Detail, StringComparer.Ordinal);
    }

    /// <summary>
    /// External text on its way into a durable file and onto a console line: collapsed to one
    /// line so it cannot forge a second, and cut so it cannot fill the screen.
    /// </summary>
    [Fact]
    public void A_status_that_would_not_fit_on_one_line_is_made_to()
    {
        var position = Tracker().Accept(
            new ModelPullProgress("pulling\r\n\tsomething " + new string('x', 200), null, 0, 0));

        Assert.DoesNotContain('\n', position!.Detail!);
        Assert.DoesNotContain('\r', position.Detail!);
        Assert.True(position.Detail!.Length <= 60, position.Detail);
    }

    /// <summary>
    /// The last line of the stream says the pull finished, and the job's own completion says that
    /// better. A phase line here would be a claim about a run that is already over.
    /// </summary>
    [Fact]
    public void The_final_success_line_is_not_a_phase()
    {
        var tracker = Tracker();
        tracker.Accept(new ModelPullProgress("pulling", "sha256:aaa", 400, 400));
        After(5);

        Assert.Null(tracker.Accept(new ModelPullProgress("success", null, 0, 0)));
    }
}
