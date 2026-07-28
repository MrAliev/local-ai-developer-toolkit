using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

public sealed class GenerationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-generation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Identity_includes_tree_model_dimension_and_format_versions()
    {
        var identity = Identity();
        var changed = identity with { RankingVersion = 2 };
        var previousNormalization = identity with { NormalizationVersion = 3 };
        var canonicalCrlf = previousNormalization with { NormalizationVersion = 4 };

        Assert.NotEqual(identity.Id, changed.Id);
        Assert.NotEqual(previousNormalization.Id, canonicalCrlf.Id);
        Assert.Equal(64, identity.Id.Length);
    }

    [Fact]
    public void Corpus_reuse_requires_the_same_normalization_and_indexing_contract()
    {
        var current = Identity() with { NormalizationVersion = 4 };

        Assert.False(current.CanReuseCorpusFrom(
            current with { NormalizationVersion = 3 }));
        Assert.False(current.CanReuseCorpusFrom(
            current with { ChunkFormatVersion = current.ChunkFormatVersion + 1 }));
        Assert.False(current.CanReuseCorpusFrom(
            current with { EmbeddingModel = "other-model" }));
        Assert.True(current.CanReuseCorpusFrom(
            current with
            {
                DevCommit = "next-commit",
                DevTree = "next-tree",
                RankingVersion = current.RankingVersion + 1
            }));
    }

    [Fact]
    public void Read_only_store_construction_does_not_create_runtime_directories()
    {
        var path = Path.Combine(_root, "not-configured");

        var current = new GenerationStore(path).ReadCurrent();

        Assert.Null(current);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Published_generation_is_immutable_and_checksum_validated()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var store = new GenerationStore(Path.Combine(_root, "repo"));
        var manifest = store.PublishIndex(source, Identity());

        Assert.Equal(manifest, store.ReadManifest(manifest.Identity.Id));
        File.AppendAllText(store.IndexPath(manifest.Identity.Id), "TAMPERED");
        Assert.Throws<InvalidDataException>(
            () => store.ReadManifest(manifest.Identity.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    internal static GenerationIdentity Identity() => new(
        "repository",
        "commit",
        "tree",
        "qwen3-embedding:8b-q8_0",
        4096,
        1,
        CodeIndex.CurrentVersion,
        1,
        1);
}
