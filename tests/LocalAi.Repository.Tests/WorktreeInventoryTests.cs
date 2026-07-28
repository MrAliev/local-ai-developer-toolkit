using LocalAi.Repository;

namespace LocalAi.Repository.Tests;

public sealed class WorktreeInventoryTests
{
    [Fact]
    public void Parses_branch_detached_and_prunable_worktrees()
    {
        var worktrees = WorktreeInventory.ParsePorcelain(
            """
            worktree C:/repo
            HEAD aaaaa
            branch refs/heads/dev

            worktree C:/repo-linked
            HEAD bbbbb
            detached
            prunable gitdir file points to non-existent location

            """);

        Assert.Equal(2, worktrees.Count);
        Assert.Equal("refs/heads/dev", worktrees[0].Branch);
        Assert.True(worktrees[1].IsDetached);
        Assert.True(worktrees[1].IsPrunable);
    }
}
