using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

public sealed class RuntimeIndexLayoutTests
{
    [Fact]
    public void Overlay_path_is_exact_for_generation_tree_and_dirty_hash()
    {
        var identity = new WorkingIndexIdentity(
            @"C:\repo-worktree",
            @"C:\repo",
            "repository",
            @"C:\runtime\repository",
            "commit",
            "tree",
            "dirty");

        var path = RuntimeIndexLayout.OverlayPath(identity, "generation");

        Assert.Contains(Path.Combine("overlays", "generation"), path);
        Assert.Contains(Path.Combine("tree", "dirty.cidx"), path);
    }

    [Fact]
    public void Clean_and_dirty_overlays_never_share_a_path()
    {
        var clean = new WorkingIndexIdentity(
            @"C:\repo-worktree",
            @"C:\repo",
            "repository",
            @"C:\runtime\repository",
            "commit",
            "tree",
            null);
        var dirty = clean with { DirtyHash = "dirty" };

        Assert.NotEqual(
            RuntimeIndexLayout.OverlayPath(clean, "generation"),
            RuntimeIndexLayout.OverlayPath(dirty, "generation"));
    }
}
