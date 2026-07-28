using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.Repository.Tests;

public sealed class RepositoryManifestStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-manifest-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_is_atomic_and_round_trips_checksum()
    {
        var store = new RepositoryManifestStore(_root);
        var manifest = Manifest();

        store.Save(manifest);

        var actual = Assert.IsType<RepositoryManifest>(store.Read());
        Assert.Equal(manifest.RepositoryId, actual.RepositoryId);
        Assert.Equal(manifest.CommonDirectory, actual.CommonDirectory);
        Assert.Equal(manifest.DevRef, actual.DevRef);
        Assert.Equal(manifest.CurrentGenerationId, actual.CurrentGenerationId);
        Assert.Equal(manifest.PublishedGitTree, actual.PublishedGitTree);
        Assert.Equal(manifest.EmbeddingModel, actual.EmbeddingModel);
        Assert.Equal(manifest.EmbeddingDimension, actual.EmbeddingDimension);
        Assert.Equal(manifest.State, actual.State);
        Assert.Equal(manifest.ActiveWorktrees, actual.ActiveWorktrees);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Tampered_manifest_is_rejected()
    {
        var store = new RepositoryManifestStore(_root);
        store.Save(Manifest());
        var path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace(
                "qwen3-embedding:8b-q8_0",
                "tampered-model",
                StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static RepositoryManifest Manifest() => new(
        "repository",
        @"C:\repo\.git",
        "refs/heads/dev",
        "generation",
        "tree",
        "qwen3-embedding:8b-q8_0",
        4096,
        1,
        1,
        RepositoryIndexState.Current,
        [new RepositoryWorktree(@"C:\repo", "commit", "refs/heads/dev")],
        DateTimeOffset.UtcNow);
}
