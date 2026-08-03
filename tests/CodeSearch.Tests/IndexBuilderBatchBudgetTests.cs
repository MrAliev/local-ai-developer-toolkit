using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

/// <summary>
/// The batch size is measured rather than configured, because the right one differs by an order
/// of magnitude between a desktop adapter and an integrated one. A constant tuned for the fast
/// machine turns into minutes-long requests on the slow one, which is where the broker watchdog
/// starts asking whether the backend is still alive.
/// </summary>
public sealed class IndexBuilderBatchBudgetTests
{
    private const int Initial = 48_000;

    [Fact]
    public void A_fast_batch_grows_the_budget_toward_the_target_duration()
    {
        // 48k characters in three seconds: the target of fifteen would take five times as much,
        // and the per-step clamp allows a doubling.
        var next = IndexBuilder.NextBudget(Initial, Initial, TimeSpan.FromSeconds(3));

        Assert.Equal(Initial * 2, next);
    }

    [Fact]
    public void A_slow_batch_shrinks_it()
    {
        // A minute for one batch is four times the target: an integrated adapter, or a queue
        // shared with somebody else's job.
        var next = IndexBuilder.NextBudget(Initial, Initial, TimeSpan.FromSeconds(60));

        Assert.Equal(Initial / 2, next);
    }

    [Fact]
    public void A_batch_already_at_the_target_leaves_it_where_it_is()
    {
        var next = IndexBuilder.NextBudget(Initial, Initial, TimeSpan.FromSeconds(15));

        Assert.Equal(Initial, next);
    }

    [Fact]
    public void The_budget_never_leaves_its_bounds()
    {
        var starved = Initial;
        for (var step = 0; step < 20; step++)
        {
            starved = IndexBuilder.NextBudget(starved, starved, TimeSpan.FromMinutes(10));
        }

        // A machine that is briefly starved must not collapse to one chunk per request.
        Assert.Equal(8_000, starved);

        var fast = Initial;
        for (var step = 0; step < 20; step++)
        {
            fast = IndexBuilder.NextBudget(fast, fast, TimeSpan.FromMilliseconds(1));
        }

        // And a fast one must not grow without limit: a single request still has to finish
        // inside the watchdog's patience, whatever the adapter.
        Assert.Equal(400_000, fast);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(48_000, 0)]
    public void A_batch_that_measured_nothing_changes_nothing(int chars, int seconds)
    {
        var next = IndexBuilder.NextBudget(Initial, chars, TimeSpan.FromSeconds(seconds));

        Assert.Equal(Initial, next);
    }
}
