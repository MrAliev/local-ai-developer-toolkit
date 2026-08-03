using LocalAi.Cli;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

/// <summary>
/// A worktree can be removed while a sync runs. It belongs to somebody else, and a sync of a
/// large repository takes tens of minutes, so this is not a rare race — it happened here, and
/// the whole run died on it after the base generation had already been embedded.
/// </summary>
public sealed class WorktreeSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-worktree-selection-" + Guid.NewGuid().ToString("N"));

    public WorktreeSelectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private GitWorktree Worktree(string name, bool create)
    {
        var path = Path.Combine(_root, name);
        if (create)
        {
            Directory.CreateDirectory(path);
        }

        return new GitWorktree(path, "abc123", "refs/heads/" + name, false, false);
    }

    [Fact]
    public void A_worktree_that_is_gone_is_skipped_and_named()
    {
        var reported = new List<string>();
        var vanished = Worktree("removed", create: false);

        var (worktrees, skipped) = CodeSearchSyncCommand.SelectPresentWorktrees(
            [Worktree("alive", create: true), vanished, Worktree("also-alive", create: true)],
            reported.Add);

        // The other two still deserve their overlays.
        Assert.Equal(2, worktrees.Count);
        Assert.DoesNotContain(worktrees, item => item.Path == vanished.Path);
        Assert.Equal(1, skipped);
        // Silently dropping it would make "fewer overlays than worktrees" indistinguishable
        // from "they were all up to date".
        Assert.Equal([vanished.Path], reported);
    }

    [Fact]
    public void Nothing_is_reported_when_every_worktree_is_still_there()
    {
        var reported = new List<string>();

        var (worktrees, skipped) = CodeSearchSyncCommand.SelectPresentWorktrees(
            [Worktree("one", create: true), Worktree("two", create: true)],
            reported.Add);

        Assert.Equal(2, worktrees.Count);
        Assert.Equal(0, skipped);
        Assert.Empty(reported);
    }
}
