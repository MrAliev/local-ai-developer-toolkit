using CodeSearch.Core.Indexing;

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
        var store = new GenerationStore(Path.Combine(_root, "repo"));
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
