using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.Repository.Tests;

public sealed class RepositoryStateMachineTests
{
    [Fact]
    public void Current_is_rejected_when_published_tree_differs()
    {
        var state = RepositoryStateMachine.Resolve(
            configured: true,
            RepositoryIndexState.Current,
            "old-tree",
            "requested-tree",
            isDirty: false,
            dirtyOverlayCurrent: false);

        Assert.Equal(RepositoryIndexState.Stale, state);
    }

    [Theory]
    [InlineData(false, false, RepositoryIndexState.Current)]
    [InlineData(true, false, RepositoryIndexState.DirtyPending)]
    [InlineData(true, true, RepositoryIndexState.DirtyCurrent)]
    public void Matching_tree_resolves_clean_and_dirty_states(
        bool dirty,
        bool dirtyCurrent,
        RepositoryIndexState expected)
    {
        var state = RepositoryStateMachine.Resolve(
            configured: true,
            RepositoryIndexState.Current,
            "tree",
            "tree",
            dirty,
            dirtyCurrent);

        Assert.Equal(expected, state);
    }

    [Fact]
    public void Unconfigured_repository_never_reports_current()
    {
        var state = RepositoryStateMachine.Resolve(
            configured: false,
            RepositoryIndexState.Current,
            "tree",
            "tree",
            isDirty: false,
            dirtyOverlayCurrent: false);

        Assert.Equal(RepositoryIndexState.NotConfigured, state);
    }
}
