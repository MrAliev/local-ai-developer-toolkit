using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class GenerationPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-publisher-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Current_switches_only_after_all_active_overlays_are_ready()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var store = new GenerationStore(FsPath.From(Path.Combine(_root, "repo")));
        var publisher = new GenerationPublisher(store);
        var identity = GenerationStoreTests.Identity();

        Assert.Throws<InvalidOperationException>(() => publisher.Publish(
            source,
            identity,
            [new OverlayReadiness("worktree", identity.Id, Ready: false)]));
        Assert.Null(store.ReadCurrent());

        publisher.Publish(
            source,
            identity,
            [new OverlayReadiness("worktree", identity.Id, Ready: true)]);

        Assert.Equal(identity.Id, store.ReadCurrent()!.GenerationId);
    }

    [Fact]
    public void Semantic_sidecar_is_published_before_current_pointer_switches()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        var semantic = Path.Combine(_root, "source.sidx");
        File.WriteAllText(source, "INDEX");
        File.WriteAllText(semantic, "SEMANTIC");
        var store = new GenerationStore(FsPath.From(Path.Combine(_root, "repo")));
        var publisher = new GenerationPublisher(store);
        var identity = GenerationStoreTests.Identity() with
        {
            SemanticIndexVersion = 1,
        };

        var manifest = publisher.Publish(source, identity, [], semantic);

        Assert.Equal("semantic.sidx", manifest.SemanticIndexFile);
        Assert.True(File.Exists(store.SemanticIndexPath(identity.Id)));
        Assert.Equal(identity.Id, store.ReadCurrent()!.GenerationId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
