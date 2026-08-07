using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class GenerationRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-generation-retention-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_current_generation_survives_however_low_the_bound_goes()
    {
        var store = new GenerationStore(Repository);
        var oldest = Publish(store, "a", Now - TimeSpan.FromDays(10));
        Publish(store, "b", Now - TimeSpan.FromDays(5));
        Publish(store, "c", Now - TimeSpan.FromDays(1));
        // The pointer names the oldest one on purpose: retention must follow the pointer, not
        // the calendar. A machine that has been on one base for a month is the normal case.
        store.SetCurrent(store.ReadManifest(oldest));

        var result = GenerationRetention.Prune(
            Repository,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 1 },
            Now);

        Assert.Equal(2, result.GenerationsRemoved.Count);
        Assert.True(Directory.Exists(Path.Combine(Repository, "generations", oldest)));
        Assert.NotNull(store.ReadManifest(oldest));
    }

    [Fact]
    public void Newest_generations_fill_the_remaining_places()
    {
        var store = new GenerationStore(Repository);
        var oldest = Publish(store, "a", Now - TimeSpan.FromDays(10));
        var middle = Publish(store, "b", Now - TimeSpan.FromDays(5));
        var newest = Publish(store, "c", Now - TimeSpan.FromDays(1));
        store.SetCurrent(store.ReadManifest(oldest));

        var result = GenerationRetention.Prune(
            Repository,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 2 },
            Now);

        Assert.Equal([middle], result.GenerationsRemoved);
        Assert.True(Directory.Exists(Path.Combine(Repository, "generations", newest)));
    }

    [Fact]
    public void Overlays_leave_with_the_generation_they_are_keyed_to()
    {
        var store = new GenerationStore(Repository);
        var kept = Publish(store, "a", Now - TimeSpan.FromDays(1));
        var dropped = Publish(store, "b", Now - TimeSpan.FromDays(9));
        store.SetCurrent(store.ReadManifest(kept));
        Overlay(kept);
        Overlay(dropped);
        Overlay("a-generation-that-no-longer-exists");

        var result = GenerationRetention.Prune(
            Repository,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 1 },
            Now);

        // An overlay is selected by generation id, so one whose generation is gone can never be
        // read again — it is dead weight the moment its base leaves.
        Assert.Equal(2, result.OverlaysRemoved.Count);
        Assert.True(Directory.Exists(Path.Combine(Repository, "overlays", kept)));
        Assert.False(Directory.Exists(Path.Combine(Repository, "overlays", dropped)));
    }

    [Fact]
    public void An_unreadable_pointer_stops_the_pass_instead_of_guessing()
    {
        var store = new GenerationStore(Repository);
        Publish(store, "a", Now - TimeSpan.FromDays(10));
        Publish(store, "b", Now - TimeSpan.FromDays(1));
        File.WriteAllText(Path.Combine(Repository, "current.json"), "{not json");

        var result = GenerationRetention.Prune(
            Repository,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 1 },
            Now);

        Assert.Equal(GenerationRetentionResult.Empty, result);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(Repository, "generations")).Length);
    }

    [Fact]
    public void Staging_leftovers_go_only_once_no_build_could_still_hold_them()
    {
        var store = new GenerationStore(Repository);
        store.SetCurrent(store.ReadManifest(Publish(store, "a", Now)));
        var staging = Path.Combine(Repository, "staging");
        Directory.CreateDirectory(staging);
        var abandoned = Path.Combine(staging, "abandoned.cidx");
        var live = Path.Combine(staging, "live.cidx");
        File.WriteAllText(abandoned, "stale");
        File.WriteAllText(live, "in progress");
        File.SetLastWriteTimeUtc(abandoned, (Now - TimeSpan.FromDays(3)).UtcDateTime);
        File.SetLastWriteTimeUtc(live, (Now - TimeSpan.FromMinutes(20)).UtcDateTime);

        var result = GenerationRetention.Prune(Repository, RuntimeRetentionPolicy.Default, Now);

        Assert.Equal(["abandoned.cidx"], result.StagingRemoved);
        Assert.True(File.Exists(live));
    }

    [Fact]
    public void Quarantined_progress_files_expire_with_the_rest_of_the_history()
    {
        var store = new GenerationStore(Repository);
        store.SetCurrent(store.ReadManifest(Publish(store, "a", Now)));
        var quarantined = Path.Combine(Repository, "progress.json.corrupt-20260807-095045");
        File.WriteAllText(quarantined, "{corrupt");
        File.SetLastWriteTimeUtc(quarantined, (Now - TimeSpan.FromDays(30)).UtcDateTime);

        var result = GenerationRetention.Prune(Repository, RuntimeRetentionPolicy.Default, Now);

        Assert.Contains("progress.json.corrupt-20260807-095045", result.StagingRemoved);
        Assert.False(File.Exists(quarantined));
    }

    [Fact]
    public void A_dry_run_reports_the_same_decisions_without_making_them()
    {
        var store = new GenerationStore(Repository);
        var kept = Publish(store, "a", Now - TimeSpan.FromDays(1));
        var dropped = Publish(store, "b", Now - TimeSpan.FromDays(9));
        store.SetCurrent(store.ReadManifest(kept));

        var result = GenerationRetention.Prune(
            Repository,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 1 },
            Now,
            dryRun: true);

        Assert.Equal([dropped], result.GenerationsRemoved);
        Assert.True(result.BytesReclaimed > 0);
        Assert.True(Directory.Exists(Path.Combine(Repository, "generations", dropped)));
    }

    private string Repository => Path.Combine(_root, "repo");

    private string Publish(GenerationStore store, string tree, DateTimeOffset publishedAtUtc)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, tree + ".cidx");
        File.WriteAllText(source, "INDEX-" + tree);
        var identity = GenerationStoreTests.Identity() with { DevTree = tree };
        return store.PublishIndex(source, identity, publishedAtUtc: publishedAtUtc)
            .Identity.Id;
    }

    private void Overlay(string generationId)
    {
        var directory = Path.Combine(Repository, "overlays", generationId, "worktree");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "clean.cidx"), "OVERLAY");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
