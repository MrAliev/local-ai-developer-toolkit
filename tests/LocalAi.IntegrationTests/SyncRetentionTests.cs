using CodeSearch.Core.Indexing;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// Publishing a generation is the only moment a repository can exceed its retention bound, so it
/// is the moment the bound has to be applied. Left to <c>localai prune</c> alone the growth was
/// invisible: superseded generations and their overlays reached hundreds of megabytes on a
/// repository that had only been committed to.
/// </summary>
public sealed class SyncRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-retention-" + Guid.NewGuid().ToString("N"));

    private string RuntimeRoot => Path.Combine(_root, "runtime");
    private string RepositoryRoot => Path.Combine(RuntimeRoot, "repositories", "repo");

    [Fact]
    public void Publishing_drops_the_generations_the_repository_has_outgrown()
    {
        var store = new GenerationStore(RepositoryRoot);
        var oldest = Publish(store, "aaa");
        var middle = Publish(store, "bbb");
        var newest = Publish(store, "ccc");
        store.SetCurrent(store.ReadManifest(newest));
        WriteRetention(generationsPerRepository: 2);

        CodeSearchSyncCommand.PruneSupersededGenerations(RepositoryRoot, RuntimeRoot);

        Assert.False(Directory.Exists(Generation(oldest)));
        Assert.True(Directory.Exists(Generation(middle)));
        Assert.True(Directory.Exists(Generation(newest)));
    }

    /// <summary>
    /// The pointer wins over the calendar. A machine that has sat on one base for a month must
    /// not have the generation it is serving deleted out from under it.
    /// </summary>
    [Fact]
    public void The_generation_being_served_is_never_dropped()
    {
        var store = new GenerationStore(RepositoryRoot);
        var oldest = Publish(store, "aaa");
        Publish(store, "bbb");
        Publish(store, "ccc");
        store.SetCurrent(store.ReadManifest(oldest));
        WriteRetention(generationsPerRepository: 1);

        CodeSearchSyncCommand.PruneSupersededGenerations(RepositoryRoot, RuntimeRoot);

        Assert.True(Directory.Exists(Generation(oldest)));
    }

    /// <summary>
    /// By the time retention runs the index is published and correct. A sweep that cannot run is
    /// worth a line on stderr and nothing more — failing the sync here would throw away a build
    /// that had just succeeded, over housekeeping.
    /// </summary>
    [Fact]
    public void A_sweep_that_cannot_run_does_not_fail_the_sync()
    {
        var missing = Path.Combine(_root, "no-such-repository");

        var failure = Record.Exception(
            () => CodeSearchSyncCommand.PruneSupersededGenerations(missing, RuntimeRoot));

        Assert.Null(failure);
    }

    private string Generation(string id) =>
        Path.Combine(RepositoryRoot, "generations", id);

    private void WriteRetention(int generationsPerRepository)
    {
        Directory.CreateDirectory(RuntimeRoot);
        new RuntimeRetentionPolicyStore(RuntimeRoot).Write(
            RuntimeRetentionPolicy.Default with
            {
                GenerationsPerRepository = generationsPerRepository,
            });
    }

    private string Publish(GenerationStore store, string tree)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, tree + ".cidx");
        File.WriteAllText(source, "INDEX-" + tree);
        return store.PublishIndex(
                source,
                new GenerationIdentity(
                    "repo",
                    "commit-" + tree,
                    tree,
                    "test-model",
                    2,
                    1,
                    CodeIndex.CurrentVersion,
                    1,
                    1))
            .Identity.Id;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
