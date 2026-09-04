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

    /// <summary>
    /// The action it names is `localai sync`, and it used to be `codesearch index`.
    ///
    /// That was the less actionable of the two on a connected repository: `codesearch index`
    /// writes an index file in place and publishes no generation, so it does not produce the
    /// thing whose absence is being reported. Both the `index_status` tool and this binary's own
    /// "Build it with `localai sync`" already said the other one, which left one binary naming
    /// two different build commands for one state.
    /// </summary>
    [Fact]
    public void MissingIndexFailsWithAnActionableMessage()
    {
        var service = new SearchService();
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.cidx");

        var ex = Assert.Throws<FileNotFoundException>(() => service.Load(missing));
        Assert.Contains("localai sync", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect #201 exists for: eviction used to run only inside Load, so the last search
    /// of a session left its index in memory for as long as the window stayed open — the
    /// request that would have evicted it never came. Time alone must be enough.
    /// </summary>
    [Fact]
    public void DropsAnIdleIndexWithoutWaitingForTheNextRequest()
    {
        var time = new FakeTime();
        using var service = new SearchService(timeProvider: time)
        {
            IdleTimeout = TimeSpan.FromMinutes(10),
        };
        service.Load(_indexPath);
        Assert.Single(service.Loaded());

        time.Advance(TimeSpan.FromMinutes(21));

        Assert.Empty(service.Loaded());
    }

    [Fact]
    public void AFreshlyUsedIndexSurvivesTheEvictionTick()
    {
        var time = new FakeTime();
        using var service = new SearchService(timeProvider: time)
        {
            IdleTimeout = TimeSpan.FromMinutes(10),
        };
        service.Load(_indexPath);
        time.Advance(TimeSpan.FromMinutes(6));
        service.Load(_indexPath);

        time.Advance(TimeSpan.FromMinutes(6));

        Assert.Single(service.Loaded());
    }

    private sealed class FakeTime : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, _now + dueTime, period);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            List<FakeTimer> timers;
            lock (_timers)
            {
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.FireDue(_now);
            }
        }

        private sealed class FakeTimer(
            TimerCallback callback,
            object? state,
            DateTimeOffset due,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset _due = due;
            private bool _disposed;

            public void FireDue(DateTimeOffset now)
            {
                while (!_disposed && _due <= now)
                {
                    callback(state);
                    if (period <= TimeSpan.Zero)
                    {
                        return;
                    }

                    _due += period;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
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
