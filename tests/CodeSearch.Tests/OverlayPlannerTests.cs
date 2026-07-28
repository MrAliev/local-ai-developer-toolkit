using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

public sealed class OverlayPlannerTests
{
    [Fact]
    public void Connected_first_parent_history_produces_commit_layers()
    {
        var plan = OverlayPlanner.Plan(
            "generation",
            "base-commit",
            "base-tree",
            [
                new CommitNode("c2", "t2", "c1", "t1"),
                new CommitNode("c1", "t1", "base-commit", "base-tree")
            ]);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, layer => Assert.Equal(OverlayKind.Commit, layer.Kind));
        Assert.Equal("base-tree", plan[0].BaseTree);
        Assert.Equal("t2", plan[1].TargetTree);
    }

    [Fact]
    public void Rewritten_or_reopened_history_collapses_against_current_dev()
    {
        var layer = Assert.Single(OverlayPlanner.Plan(
            "generation",
            "new-dev",
            "new-tree",
            [new CommitNode("branch", "branch-tree", "old-dev", "old-tree")]));

        Assert.Equal(OverlayKind.Collapsed, layer.Kind);
        Assert.Equal("new-tree", layer.BaseTree);
    }

    [Fact]
    public void Dirty_overlay_identity_changes_with_content_hash()
    {
        var first = OverlayPlanner.Dirty("g", "tree", "commit", "hash-1");
        var second = OverlayPlanner.Dirty("g", "tree", "commit", "hash-2");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(OverlayKind.Dirty, first.Kind);
    }
}
