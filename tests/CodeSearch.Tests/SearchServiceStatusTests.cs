using System.Diagnostics;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace CodeSearch.Tests;

public sealed class SearchServiceStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-status-" + Guid.NewGuid().ToString("N"));

    /// <summary>A runtime of this test's own, so a concurrent sync cannot compete with it.</summary>
    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "codesearch-status-runtime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Generation_status_tracks_the_manifest_mainline_ref()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "A.cs");
        File.WriteAllText(sourcePath, "class A {}\r\n");
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@local.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("add", "A.cs");
        Git("commit", "-m", "Initial");
        Git("branch", "dev");
        var devIdentity = RuntimeIndexLayout.Inspect(_root, _runtimeRoot);

        Git("switch", "-c", "feature");
        File.WriteAllText(sourcePath, "class A { int Feature; }\r\n");
        Git("add", "A.cs");
        Git("commit", "-m", "Feature");
        var featureIdentity = RuntimeIndexLayout.Inspect(_root, _runtimeRoot);

        var generation = new GenerationIdentity(
            featureIdentity.RepositoryId,
            devIdentity.HeadCommit,
            devIdentity.HeadTree,
            "test-model",
            2,
            1,
            CodeIndex.CurrentVersion,
            4,
            1);
        var sourceIndex = Path.Combine(Path.GetTempPath(), generation.Id + ".cidx");
        try
        {
            new CodeIndex
            {
                Dim = 2,
                Model = "test-model",
                Root = _root,
                GitCommit = devIdentity.HeadCommit,
                GitTree = devIdentity.HeadTree,
                RepositoryId = featureIdentity.RepositoryId,
                GenerationId = generation.Id,
                IndexedAtUtc = DateTime.UtcNow,
                Files = [],
                Chunks = [],
                Vectors = []
            }.Save(sourceIndex);
            var store = new GenerationStore(featureIdentity.RepositoryRuntimeRoot);
            var manifest = store.PublishIndex(sourceIndex, generation);
            store.SetCurrent(manifest);
        }
        finally
        {
            File.Delete(sourceIndex);
        }

        new RepositoryManifestStore(featureIdentity.RepositoryRuntimeRoot).Save(
            new RepositoryManifest(
                featureIdentity.RepositoryId,
                RepoLocator.GitOutput(
                    _root,
                    "rev-parse --path-format=absolute --git-common-dir")!,
                "refs/heads/dev",
                generation.Id,
                devIdentity.HeadTree,
                generation.EmbeddingModel,
                generation.EmbeddingDimension,
                generation.ChunkFormatVersion,
                generation.IndexFormatVersion,
                RepositoryIndexState.Current,
                [new RepositoryWorktree(_root, featureIdentity.HeadCommit, "refs/heads/feature")],
                DateTimeOffset.UtcNow));

        Assert.False(new SearchService(runtimeRoot: _runtimeRoot).Status(_root).CommitDrifted);

        Git("branch", "-f", "dev", "feature");

        Assert.True(new SearchService(runtimeRoot: _runtimeRoot).Status(_root).CommitDrifted);
    }

    [Fact]
    public void Dirty_base_checkout_is_not_reported_as_needing_no_overlay()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "A.cs");
        File.WriteAllText(sourcePath, "class A {}\r\n");
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@local.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("add", "A.cs");
        Git("commit", "-m", "Initial");

        var identity = RuntimeIndexLayout.Inspect(_root, _runtimeRoot);
        var generation = new GenerationIdentity(
            identity.RepositoryId,
            identity.HeadCommit,
            identity.HeadTree,
            "test-model",
            2,
            1,
            CodeIndex.CurrentVersion,
            3,
            1);
        var sourceIndex = Path.Combine(Path.GetTempPath(), generation.Id + ".cidx");
        try
        {
            new CodeIndex
            {
                Dim = 2,
                Model = "test-model",
                Root = _root,
                GitCommit = identity.HeadCommit,
                GitTree = identity.HeadTree,
                RepositoryId = identity.RepositoryId,
                GenerationId = generation.Id,
                IndexedAtUtc = DateTime.UtcNow,
                Files = [],
                Chunks = [],
                Vectors = []
            }.Save(sourceIndex);
            var store = new GenerationStore(identity.RepositoryRuntimeRoot);
            var manifest = store.PublishIndex(sourceIndex, generation);
            store.SetCurrent(manifest);
        }
        finally
        {
            File.Delete(sourceIndex);
        }

        var cleanStatus = new SearchService(runtimeRoot: _runtimeRoot).Status(_root);
        Assert.False(cleanStatus.RequiresOverlay);
        Assert.True(cleanStatus.WorkingRootIsBase);

        File.WriteAllText(sourcePath, "class A { int Value; }\r\n");

        var status = new SearchService(runtimeRoot: _runtimeRoot).Status(_root);

        Assert.True(status.RequiresOverlay);
        Assert.False(status.WorkingRootIsBase);
        Assert.False(status.Overlay.Exists);

        var dirtyIdentity = RuntimeIndexLayout.Inspect(_root, _runtimeRoot);
        var overlayPath = RuntimeIndexLayout.OverlayPath(dirtyIdentity, generation.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
        new CodeIndex
        {
            Dim = 2,
            Model = "test-model",
            Root = _root,
            GitCommit = dirtyIdentity.HeadCommit,
            GitTree = dirtyIdentity.HeadTree,
            RepositoryId = dirtyIdentity.RepositoryId,
            GenerationId = generation.Id,
            DirtyHash = dirtyIdentity.DirtyHash,
            BaseCommit = identity.HeadCommit,
            IndexedAtUtc = DateTime.UtcNow,
            Files = [],
            Chunks = [],
            Vectors = []
        }.Save(overlayPath);

        var overlayStatus = new SearchService(runtimeRoot: _runtimeRoot).Status(_root);
        Assert.True(overlayStatus.RequiresOverlay);
        Assert.True(overlayStatus.Overlay.Exists);
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
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

    public void Dispose()
    {
        DeleteTree(_runtimeRoot);
        DeleteTree(_root);
    }

    private static void DeleteTree(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
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
}
