using CodeSearch.Core.Chunking;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;

namespace CodeSearch.Tests;

public class SearchServiceCacheTests : IDisposable
{
    private readonly string _indexPath = Path.Combine(Path.GetTempPath(), $"codesearch-cache-{Guid.NewGuid():N}.cidx");

    public SearchServiceCacheTests() => BuildIndex().Save(_indexPath);

    [Fact]
    public void KeepsTheIndexInMemoryWhileItIsBeingUsed()
    {
        var service = new SearchService { IdleTimeout = TimeSpan.FromHours(1) };

        var first = service.Load(_indexPath);
        var second = service.Load(_indexPath);

        Assert.Same(first, second);
        Assert.Single(service.Loaded());
    }

    [Fact]
    public void DropsAnIndexNobodyHasSearchedRecently()
    {
        // The scenario this exists for: a Claude Code session left open after one search would
        // otherwise pin ~700MB for as long as the window stays open.
        var service = new SearchService { IdleTimeout = TimeSpan.Zero };

        var first = service.Load(_indexPath);
        var second = service.Load(_indexPath);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void UnloadAllEmptiesTheCacheAndReportsWhatItFreed()
    {
        var service = new SearchService { IdleTimeout = TimeSpan.FromHours(1) };
        service.Load(_indexPath);

        Assert.Single(service.Loaded());

        service.UnloadAll();

        Assert.Empty(service.Loaded());
    }

    [Fact]
    public void ReloadsWhenTheFileOnDiskWasRebuilt()
    {
        var service = new SearchService { IdleTimeout = TimeSpan.FromHours(1) };
        var first = service.Load(_indexPath);

        // A reindex rewrites the file; serving the old vectors afterwards would silently answer
        // for code that no longer looks like that.
        var rebuilt = BuildIndex();
        Thread.Sleep(10);
        rebuilt.Save(_indexPath);

        Assert.NotSame(first, service.Load(_indexPath));
    }

    [Fact]
    public void MissingIndexFailsWithAnActionableMessage()
    {
        var service = new SearchService();
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.cidx");

        var ex = Assert.Throws<FileNotFoundException>(() => service.Load(missing));
        Assert.Contains("codesearch index", ex.Message);
    }

    private static CodeIndex BuildIndex() => new()
    {
        Dim = 4,
        Model = "test-model",
        Root = Path.GetTempPath(),
        GitCommit = "abc123",
        IndexedAtUtc = DateTime.UtcNow,
        Files = [new IndexedFile { RelPath = "A.cs", Hash = new byte[32], ChunkStart = 0, ChunkCount = 1 }],
        Chunks =
        [
            new ChunkMeta
            {
                FileIndex = 0,
                Kind = ChunkKind.Type,
                Symbol = "A",
                Signature = "public class A",
                Namespace = "Ns",
                StartLine = 1,
                EndLine = 5,
            },
        ],
        Vectors = [1, 0, 0, 0],
    };

    public void Dispose()
    {
        try
        {
            File.Delete(_indexPath);
        }
        catch (IOException)
        {
        }
    }
}
