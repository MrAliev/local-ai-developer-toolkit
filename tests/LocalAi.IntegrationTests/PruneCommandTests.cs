using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

public sealed class PruneCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-prune-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_repository_whose_checkout_is_gone_is_forgotten()
    {
        // The usual source of these is a throwaway worktree: it indexes once, is deleted, and
        // its record then sits in the runtime forever reporting a state that will never change.
        var repository = Repository("dead", Path.Combine(_root, "never-existed", ".git"));

        var report = PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.False(Directory.Exists(repository));
        Assert.Contains(report.Lines, line => line.Contains("abandoned record removed"));
    }

    [Fact]
    public void A_repository_that_is_still_on_disk_is_left_alone()
    {
        var commonDirectory = Path.Combine(_root, "live", ".git");
        Directory.CreateDirectory(commonDirectory);
        var repository = Repository(
            "live",
            commonDirectory,
            state: RepositoryIndexState.Current,
            updatedAtUtc: Now - TimeSpan.FromDays(400));

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        // An index nobody has touched in a year is not an abandoned one. It is a repository
        // whose mainline has not moved, and its generation is still exactly what a search wants.
        Assert.True(Directory.Exists(repository));
    }

    [Fact]
    public void A_record_still_inside_the_retention_window_keeps_its_place()
    {
        var commonDirectory = Path.Combine(_root, "new", ".git");
        Directory.CreateDirectory(commonDirectory);
        var repository = Repository(
            "new",
            commonDirectory,
            updatedAtUtc: Now - TimeSpan.FromHours(2));

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        // Still indexing is not the same as abandoned, and a first generation takes hours.
        Assert.True(Directory.Exists(repository));
    }

    [Fact]
    public void The_legacy_index_directory_is_not_a_repository_record()
    {
        var legacy = Path.Combine(Runtime, "repositories", "legacy");
        Directory.CreateDirectory(legacy);

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        // It predates generations, is keyed by path rather than repository identity, and has no
        // manifest by design. Treating it as a broken record would delete a live code path's
        // storage.
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void Installed_versions_keep_the_current_one_and_the_newest_predecessors()
    {
        Version("oldest", Now - TimeSpan.FromDays(60));
        Version("old", Now - TimeSpan.FromDays(30));
        var previous = Version("previous", Now - TimeSpan.FromDays(2));
        var current = Version("current", Now - TimeSpan.FromDays(10));
        WriteCurrentPointer("current");

        var report = PruneCommand.Execute(Runtime, dryRun: false, Now);

        // Three places: the pointer takes one, the two newest predecessors take the rest, and
        // the fourth version is the only one with nothing left to roll back to.
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(previous));
        Assert.True(Directory.Exists(Path.Combine(Runtime, "bin", "versions", "old")));
        Assert.False(Directory.Exists(Path.Combine(Runtime, "bin", "versions", "oldest")));
        Assert.Contains(report.Lines, line => line.StartsWith("versions:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_current_version_survives_even_when_it_is_the_oldest_on_disk()
    {
        var current = Version("current", Now - TimeSpan.FromDays(90));
        Version("newer-1", Now - TimeSpan.FromDays(2));
        Version("newer-2", Now - TimeSpan.FromDays(1));
        Version("newer-3", Now);
        WriteCurrentPointer("current");

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.True(Directory.Exists(current));
        Assert.Equal(
            RuntimeRetentionPolicy.Default.InstalledVersions,
            Directory.GetDirectories(Path.Combine(Runtime, "bin", "versions")).Length);
    }

    [Fact]
    public void Versions_are_skipped_when_the_pointer_cannot_be_trusted()
    {
        Version("a", Now - TimeSpan.FromDays(30));
        Version("b", Now - TimeSpan.FromDays(20));
        Version("c", Now - TimeSpan.FromDays(10));
        Version("d", Now);
        Directory.CreateDirectory(Path.Combine(Runtime, "bin"));
        File.WriteAllText(Path.Combine(Runtime, "bin", "current.json"), "{not a pointer");

        var report = PruneCommand.Execute(Runtime, dryRun: false, Now);

        // Without a trustworthy pointer there is no way to name the version in use, and
        // guessing wrong takes the running installation with it.
        Assert.Equal(4, Directory.GetDirectories(Path.Combine(Runtime, "bin", "versions")).Length);
        Assert.Contains(report.Lines, line => line.Contains("versions: skipped"));
    }

    [Fact]
    public void Launcher_backups_are_capped()
    {
        WriteCurrentPointer("current");
        Version("current", Now);
        for (var index = 0; index < 8; index++)
        {
            var backup = Path.Combine(
                Runtime,
                "installer",
                "backups",
                $"launcher-{index:D2}");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "localai-launcher.exe"), "binary");
            Directory.SetLastWriteTimeUtc(
                backup,
                (Now - TimeSpan.FromDays(index)).UtcDateTime);
        }

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.Equal(
            RuntimeRetentionPolicy.Default.LauncherBackups,
            Directory.GetDirectories(Path.Combine(Runtime, "installer", "backups")).Length);
    }

    [Fact]
    public void A_dry_run_removes_nothing()
    {
        var repository = Repository("dead", Path.Combine(_root, "never-existed", ".git"));
        Version("old", Now - TimeSpan.FromDays(30));
        Version("current", Now);
        WriteCurrentPointer("current");

        var report = PruneCommand.Execute(Runtime, dryRun: true, Now);

        Assert.True(Directory.Exists(repository));
        Assert.True(Directory.Exists(Path.Combine(Runtime, "bin", "versions", "old")));
        Assert.True(report.BytesReclaimed > 0);
    }

    private string Runtime => Path.Combine(_root, "runtime");

    private string Repository(
        string name,
        string commonDirectory,
        DateTimeOffset? updatedAtUtc = null,
        RepositoryIndexState state = RepositoryIndexState.Initializing)
    {
        var directory = Path.Combine(Runtime, "repositories", name.PadRight(16, '0'));
        Directory.CreateDirectory(directory);
        new RepositoryManifestStore(directory).Save(new RepositoryManifest(
            name.PadRight(16, '0'),
            commonDirectory,
            "refs/heads/main",
            null,
            null,
            "qwen3-embedding:8b-q8_0",
            4096,
            1,
            4,
            state,
            [],
            updatedAtUtc ?? Now - TimeSpan.FromDays(40)));
        return directory;
    }

    private string Version(string name, DateTimeOffset installedAtUtc)
    {
        var directory = Path.Combine(Runtime, "bin", "versions", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "localai.exe"), "binary");
        Directory.SetLastWriteTimeUtc(directory, installedAtUtc.UtcDateTime);
        return directory;
    }

    private void WriteCurrentPointer(string version)
    {
        var binRoot = Path.Combine(Runtime, "bin");
        Directory.CreateDirectory(binRoot);
        File.WriteAllBytes(
            Path.Combine(binRoot, "current.json"),
            CurrentPointerSnapshot.CreateCanonicalBytes(version));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
