using CodeSearch.Core.Indexing;
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
        Assert.Contains(report.Lines, line => line.Contains("its checkout no longer exists"));
    }

    /// <summary>
    /// A drive that is not mounted right now looks exactly like a deleted checkout to
    /// <see cref="Directory.Exists(string)"/>, and the difference is a repository's whole index.
    ///
    /// The overlay pass already knows this — `IsGone` was written for it, and its comment names
    /// the unmounted volume, the disconnected share and the absent subst drive. The abandoned
    /// check four hundred lines away did not ask, so an external disk left unplugged cost the
    /// record, its manifest and every generation under it: hours of embedding, reported as
    /// reclaimed megabytes.
    /// </summary>
    [Fact]
    public void A_repository_whose_drive_is_offline_is_left_alone()
    {
        // A volume this session has no such drive letter for. Directory.Exists says false for the
        // path exactly as it would for a deleted checkout.
        var offline = OfflineDriveLetter();
        Assert.False(Directory.Exists(offline + @":\"), "the test needs a drive letter nothing uses");
        var repository = Repository("offline", offline + @":\checkout\.git");

        var report = PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.True(
            Directory.Exists(repository),
            "a record was deleted because its drive happened to be offline");
        Assert.DoesNotContain(report.Lines, line => line.Contains("its checkout no longer exists"));
    }

    private static char OfflineDriveLetter()
    {
        var taken = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!taken.Contains(letter))
            {
                return letter;
            }
        }

        throw new InvalidOperationException("every drive letter is in use on this machine");
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
    public void Job_telemetry_past_its_retention_is_dropped()
    {
        var metrics = Path.Combine(Runtime, "telemetry", "metrics");
        Directory.CreateDirectory(metrics);
        var old = TelemetryRecord(metrics, Now - TimeSpan.FromDays(45));
        var recent = TelemetryRecord(metrics, Now - TimeSpan.FromDays(3));
        // Age is read from the name, so a name that does not carry one is left alone rather than
        // guessed at — a foreign file in this directory is not the prune's to interpret.
        var foreign = Path.Combine(metrics, "notes.json");
        File.WriteAllText(foreign, "{}");

        var report = PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
        Assert.Contains(report.Lines, line => line.StartsWith("telemetry:", StringComparison.Ordinal));
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

    /// <summary>
    /// The overlays under a generation that is being kept are the bulk of what accumulates —
    /// one per worktree, and inside it one per tree that worktree's HEAD has ever been on — and
    /// the command the documentation names for reclaiming space walked straight past all of
    /// them. On this machine that was 1.4 GB the sweep reported as nothing to do.
    /// </summary>
    [Fact]
    public void Overlays_nothing_can_read_are_collected_from_a_generation_that_stays()
    {
        var worktree = NewCheckout("live");
        var identity = RuntimeIndexLayout.Inspect(worktree, Runtime);
        LiveRepository(identity, worktree);

        var current = Overlay(identity.RepositoryRuntimeRoot.Value, Generation, identity, "clean.cidx");
        var pastCommit = Overlay(
            identity.RepositoryRuntimeRoot.Value,
            Generation,
            RuntimeIndexLayout.WorktreeKey(worktree),
            new string('a', 40),
            "clean.cidx");
        var goneWorktree = Overlay(
            identity.RepositoryRuntimeRoot.Value,
            Generation,
            RuntimeIndexLayout.WorktreeKey(Path.Combine(_root, "deleted-worktree")),
            identity.HeadTree,
            "clean.cidx");

        var report = PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.True(File.Exists(current));
        Assert.False(File.Exists(pastCommit));
        Assert.False(File.Exists(goneWorktree));
        Assert.True(report.BytesReclaimed > 0);
    }

    /// <summary>
    /// A worktree that cannot be inspected is not a worktree that is gone. Guessing there costs
    /// a live checkout its index; leaving the overlays alone costs disk until the next sync.
    /// </summary>
    [Fact]
    public void A_worktree_that_cannot_be_inspected_stops_the_sweep_rather_than_guessing()
    {
        var worktree = NewCheckout("live");
        var identity = RuntimeIndexLayout.Inspect(worktree, Runtime);
        // Recorded, present on disk, and not a git checkout: Inspect throws rather than
        // answering, which is the case that must not be read as "nothing is reachable".
        var opaque = Path.Combine(_root, "not-a-checkout");
        Directory.CreateDirectory(opaque);
        // Proved rather than assumed: were the temp directory itself inside a git repository,
        // the walk up from here would find that .git and answer instead of throwing, and this
        // test would pass while exercising the branch it exists to rule out.
        Assert.ThrowsAny<Exception>(() => RuntimeIndexLayout.Inspect(opaque, Runtime));
        LiveRepository(identity, worktree, opaque);

        var overlay = Overlay(
            identity.RepositoryRuntimeRoot.Value,
            Generation,
            RuntimeIndexLayout.WorktreeKey(worktree),
            new string('a', 40),
            "clean.cidx");

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.True(File.Exists(overlay));
    }

    /// <summary>
    /// A manifest nobody can read stops this repository's overlays being touched — it does not
    /// stop the sweep. Leaving InvalidDataException out of the filter did the second: it escaped
    /// Execute, so telemetry, installed versions and launcher backups were never reached, and a
    /// single corrupt file turned the whole command into an error exit.
    /// </summary>
    [Fact]
    public void A_manifest_that_does_not_verify_costs_its_own_repository_and_no_more()
    {
        var worktree = NewCheckout("live");
        var identity = RuntimeIndexLayout.Inspect(worktree, Runtime);
        LiveRepository(identity, worktree);
        var overlay = Overlay(
            identity.RepositoryRuntimeRoot.Value,
            Generation,
            RuntimeIndexLayout.WorktreeKey(worktree),
            new string('a', 40),
            "clean.cidx");
        Corrupt(identity.RepositoryRuntimeRoot.Value);
        Version("oldest", Now - TimeSpan.FromDays(60));
        Version("old", Now - TimeSpan.FromDays(30));
        Version("previous", Now - TimeSpan.FromDays(2));
        Version("current", Now);
        WriteCurrentPointer("current");

        var report = PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.True(File.Exists(overlay));
        Assert.False(Directory.Exists(Path.Combine(Runtime, "bin", "versions", "oldest")));
        Assert.Contains(report.Lines, line => line.StartsWith("versions:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A manifest recording no worktrees cannot answer what is reachable, and an empty answer is
    /// read by retention as "nothing is" — which removes every overlay under every generation it
    /// is keeping.
    /// </summary>
    [Fact]
    public void A_manifest_naming_no_worktrees_is_no_answer_rather_than_an_empty_one()
    {
        var worktree = NewCheckout("live");
        var identity = RuntimeIndexLayout.Inspect(worktree, Runtime);
        LiveRepository(identity);
        var overlay = Overlay(identity.RepositoryRuntimeRoot.Value, Generation, identity, "clean.cidx");

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.True(File.Exists(overlay));
    }

    /// <summary>
    /// The overlay pass obeys --dry-run like every other. The dry run test that predates overlays
    /// creates none, so nothing covered the branch that now does the bulk of the removing.
    /// </summary>
    [Fact]
    public void A_dry_run_reports_the_overlays_it_would_collect_without_collecting_them()
    {
        var worktree = NewCheckout("live");
        var identity = RuntimeIndexLayout.Inspect(worktree, Runtime);
        LiveRepository(identity, worktree);
        var pastCommit = Overlay(
            identity.RepositoryRuntimeRoot.Value,
            Generation,
            RuntimeIndexLayout.WorktreeKey(worktree),
            new string('a', 40),
            "clean.cidx");

        var report = PruneCommand.Execute(Runtime, dryRun: true, Now);

        Assert.True(File.Exists(pastCommit));
        Assert.True(report.BytesReclaimed > 0);
    }

    /// <summary>
    /// A worktree on a volume that is not mounted is not a worktree that was deleted, and
    /// Directory.Exists gives the same false for both. Reading that false as a deletion collects
    /// the index of every checkout on a drive that happened to be offline when the sweep ran —
    /// an hour of re-embedding each, for a machine whose second drive was simply not plugged in.
    /// </summary>
    [Fact]
    public void A_worktree_on_a_volume_that_is_not_mounted_is_doubt_rather_than_a_deletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only Windows has a volume that can be absent from a path's root.");
        }

        var absent = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => $"{(char)letter}:\\")
            .FirstOrDefault(root => !Directory.Exists(root));
        if (absent is null)
        {
            Assert.Skip("Every drive letter is in use, so no absent volume can be named.");
        }

        var worktree = NewCheckout("live");
        var identity = RuntimeIndexLayout.Inspect(worktree, Runtime);
        LiveRepository(identity, worktree, Path.Combine(absent, "offline", "worktree"));
        var overlay = Overlay(
            identity.RepositoryRuntimeRoot.Value,
            Generation,
            RuntimeIndexLayout.WorktreeKey(worktree),
            new string('a', 40),
            "clean.cidx");

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        // Not just the offline worktree's overlays: the repository's whole overlay pass is off,
        // because one worktree that cannot be established makes the answer unknown.
        Assert.True(File.Exists(overlay));
    }

    /// <summary>Leaves the manifest present and unreadable, the way a half-written file is.</summary>
    private static void Corrupt(string repositoryRuntimeRoot)
    {
        var path = Path.Combine(repositoryRuntimeRoot, "manifest.json");
        var document = File.ReadAllText(path);
        var checksum = document.IndexOf("\"checksum\"", StringComparison.OrdinalIgnoreCase);
        Assert.True(checksum >= 0, "the manifest is expected to carry a checksum");
        var start = document.IndexOf('"', document.IndexOf(':', checksum)) + 1;
        File.WriteAllText(
            path,
            document[..start] + new string('0', 64) + document[(start + 64)..]);
    }

    private const string Generation = "gen-0000000000000000";

    /// <summary>A repository whose generation is current and whose worktrees are recorded.</summary>
    private void LiveRepository(
        WorkingIndexIdentity identity,
        params string[] worktrees)
    {
        var root = identity.RepositoryRuntimeRoot.Value;
        Directory.CreateDirectory(Path.Combine(root, "generations", Generation));
        File.WriteAllText(
            Path.Combine(root, "generations", Generation, "base.cidx"),
            new string('x', 4096));
        File.WriteAllText(
            Path.Combine(root, "current.json"),
            System.Text.Json.JsonSerializer.Serialize(
                new GenerationPointer(Generation, identity.HeadTree, Now),
                LocalAiJson.Strict));
        new RepositoryManifestStore(FsPath.From(root)).Save(new RepositoryManifest(
            Path.GetFileName(root),
            identity.RepositoryRoot.Value,
            "refs/heads/main",
            Generation,
            identity.HeadTree,
            "qwen3-embedding:8b-q8_0",
            4096,
            1,
            4,
            RepositoryIndexState.Current,
            [.. worktrees.Select(path => new RepositoryWorktree(path, "head", "refs/heads/main"))],
            Now - TimeSpan.FromHours(1)));
    }

    private static string Overlay(
        string repositoryRuntimeRoot,
        string generation,
        WorkingIndexIdentity identity,
        string file) =>
        Overlay(
            repositoryRuntimeRoot,
            generation,
            RuntimeIndexLayout.WorktreeKey(identity.WorkingRoot),
            identity.HeadTree,
            file);

    private static string Overlay(
        string repositoryRuntimeRoot,
        string generation,
        string worktreeId,
        string headTree,
        string file)
    {
        var directory = Path.Combine(
            repositoryRuntimeRoot,
            "overlays",
            generation,
            worktreeId,
            headTree);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, file);
        File.WriteAllText(path, new string('x', 2048));
        return path;
    }

    /// <summary>A real checkout, because the reachable set is read out of git.</summary>
    private string NewCheckout(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "A.cs"), "class A { }");
        Git(directory, "init", "-b", "main");
        Git(directory, "config", "user.email", "tests@local.invalid");
        Git(directory, "config", "user.name", "LocalAi Tests");
        Git(directory, "add", "A.cs");
        Git(directory, "commit", "-m", "Initial");
        return directory;
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private string Runtime => Path.Combine(_root, "runtime");

    /// <summary>
    /// Every repository id is 64 hex characters, so a record whose directory name is shorter than
    /// twelve was made by hand — and the report slices that name to twelve. The slice threw
    /// `ArgumentOutOfRangeException`, which no filter in this file catches, so one stray record
    /// ended every prune on the machine: telemetry, installed versions and launcher backups are
    /// swept after the repository loop and were never reached.
    ///
    /// A manifest is what makes this reachable. Without one the record is not classified at all
    /// and the loop moves on before the slice — which is why a bare directory does not reproduce
    /// it.
    /// </summary>
    [Fact]
    public void A_record_with_a_short_name_does_not_end_the_run()
    {
        Record("sh", Path.Combine(_root, "never-existed", ".git"));
        var dead = Repository("dead", Path.Combine(_root, "also-never-existed", ".git"));

        PruneCommand.Execute(Runtime, dryRun: false, Now);

        // The record after the short one was still swept.
        Assert.False(Directory.Exists(dead));
    }

    /// <summary>And the short record is reported by the name it actually has.</summary>
    [Fact]
    public void A_record_with_a_short_name_is_named_by_it()
    {
        Record("sh", Path.Combine(_root, "never-existed", ".git"));

        var report = PruneCommand.Execute(Runtime, dryRun: false, Now);

        Assert.Contains(report.Lines, line => line.Contains("sh", StringComparison.Ordinal));
    }

    /// <summary>
    /// A repository record under the name given, without the padding <see cref="Repository"/>
    /// applies — which is what makes a name shorter than the report's slice reachable at all.
    /// </summary>
    private string Record(string name, string commonDirectory)
    {
        var directory = Path.Combine(Runtime, "repositories", name);
        Directory.CreateDirectory(directory);
        new RepositoryManifestStore(FsPath.From(directory)).Save(new RepositoryManifest(
            name,
            commonDirectory,
            "refs/heads/main",
            null,
            null,
            "qwen3-embedding:8b-q8_0",
            4096,
            1,
            4,
            RepositoryIndexState.Initializing,
            [],
            Now - TimeSpan.FromDays(40)));
        return directory;
    }

    private string Repository(
        string name,
        string commonDirectory,
        DateTimeOffset? updatedAtUtc = null,
        RepositoryIndexState state = RepositoryIndexState.Initializing)
    {
        var directory = Path.Combine(Runtime, "repositories", name.PadRight(16, '0'));
        Directory.CreateDirectory(directory);
        new RepositoryManifestStore(FsPath.From(directory)).Save(new RepositoryManifest(
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

    private static string TelemetryRecord(string directory, DateTimeOffset recordedAtUtc)
    {
        var path = Path.Combine(
            directory,
            $"{recordedAtUtc.UtcTicks:D19}-{Guid.NewGuid():N}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        return path;
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
        if (!Directory.Exists(_root))
        {
            return;
        }

        // Git marks everything under .git/objects read-only, and a recursive delete stops on the
        // first one. A fixture that cannot clean up fails every test that built a checkout.
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup that throws fails the test it was added to protect. A temp directory left
            // behind is the smaller problem, and the next run picks a fresh one anyway.
        }
    }
}
