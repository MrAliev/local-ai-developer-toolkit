using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class SearchReadinessGateTests
{
    [Fact]
    public async Task Concurrent_stale_searches_join_one_matching_repair()
    {
        var gate = new SearchReadinessGate();
        var requirement = Requirement();
        var stale = Ready() with { State = RepositoryIndexState.Stale };
        var repairs = 0;
        async Task<SearchReadiness> Repair(CancellationToken _)
        {
            Interlocked.Increment(ref repairs);
            await Task.Delay(20, TestContext.Current.CancellationToken);
            return Ready();
        }

        await Task.WhenAll(
            gate.EnsureAsync(
                stale,
                requirement,
                Repair,
                TestContext.Current.CancellationToken),
            gate.EnsureAsync(
                stale,
                requirement,
                Repair,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, repairs);
    }

    [Fact]
    public async Task Mismatched_tree_is_never_accepted_as_current()
    {
        var gate = new SearchReadinessGate();
        var wrong = Ready() with { GitTree = "wrong-tree" };

        await Assert.ThrowsAsync<SearchNotReadyException>(
            () => gate.EnsureAsync(
                wrong,
                Requirement(),
                _ => Task.FromResult(wrong),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_state_explains_all_three_owner_fallbacks()
    {
        var gate = new SearchReadinessGate();
        var error = await Assert.ThrowsAsync<SearchNotReadyException>(
            () => gate.EnsureAsync(
                Ready() with { State = RepositoryIndexState.Failed },
                Requirement(),
                _ => Task.FromResult(Ready()),
                TestContext.Current.CancellationToken));

        Assert.Contains("restart MCP", error.Message);
        Assert.Contains("CLI through the broker", error.Message);
        Assert.Contains("rg", error.Message);
    }

    private static SearchRequirement Requirement() => new(
        "repository",
        "generation",
        "tree",
        null,
        "qwen3-embedding:8b-q8_0",
        4096,
        1,
        CodeIndex.CurrentVersion);

    private static SearchReadiness Ready() => new(
        RepositoryIndexState.Current,
        "repository",
        "generation",
        "tree",
        null,
        "qwen3-embedding:8b-q8_0",
        4096,
        1,
        CodeIndex.CurrentVersion);
}
