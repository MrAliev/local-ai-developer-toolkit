using System.Diagnostics;
using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

public sealed class RuntimeIndexLayoutTests
{
    /// <summary>
    /// The guarantee the runtime-root parameter exists for, asserted rather than assumed: a
    /// caller that names an installation gets every path inside it, and nothing lands in the
    /// machine's own. Without this, an injected path could quietly go back to resolving
    /// %LOCALAPPDATA%\LocalAi and the only symptom would be tests failing when something else
    /// happens to be indexing.
    /// </summary>
    [Fact]
    public void A_named_runtime_root_holds_every_path_that_belongs_to_it()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "codesearch-layout-" + Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "codesearch-layout-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        try
        {
            File.WriteAllText(Path.Combine(repository, "A.cs"), "class A {}\r\n");
            Git(repository, "init", "-b", "main");
            Git(repository, "config", "user.email", "tests@local.invalid");
            Git(repository, "config", "user.name", "LocalAi Tests");
            Git(repository, "add", "A.cs");
            Git(repository, "commit", "-m", "Initial");

            var named = RuntimeIndexLayout.Inspect(repository, runtimeRoot);
            var machine = RuntimeIndexLayout.Inspect(repository);

            Assert.StartsWith(
                runtimeRoot,
                named.RepositoryRuntimeRoot,
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(
                runtimeRoot,
                RuntimeIndexLayout.OverlayPath(named, "generation"),
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(
                runtimeRoot,
                RepoLocator.LegacyIndexPathFor(repository, runtimeRoot),
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(
                runtimeRoot,
                RepoLocator.IndexPathFor(repository, runtimeRoot),
                StringComparison.OrdinalIgnoreCase);

            // Same repository, same identity - only the installation differs. Otherwise the
            // parameter would be redirecting more than it claims to.
            Assert.Equal(machine.RepositoryId, named.RepositoryId);
            Assert.Equal(machine.HeadTree, named.HeadTree);
            Assert.StartsWith(
                RuntimeIndexLayout.DefaultRuntimeRoot,
                machine.RepositoryRuntimeRoot,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTree(runtimeRoot);
            DeleteTree(repository);
        }
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

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
