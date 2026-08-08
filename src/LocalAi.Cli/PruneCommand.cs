using CodeSearch.Core.Indexing;
using LocalAi.Broker;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Repository;
using System.Text.Json;

namespace LocalAi.Cli;

public sealed record PruneReport(
    RetentionSweepResult Archive,
    IReadOnlyList<string> Lines,
    long BytesReclaimed);

/// <summary>
/// Applies the retention bounds on demand, across everything the runtime root accumulates.
///
/// The broker enforces its own archive bound continuously, so this exists for the two things a
/// background sweep cannot do: reclaim a backlog that built up before any bound existed, and
/// reach the parts of the runtime the broker does not own — repository generations, installed
/// versions and launcher backups.
///
/// Nothing here removes something in use. The generation named by a repository's pointer, the
/// version named by <c>bin/current.json</c>, and any job that has not been terminal for longer
/// than the response grace are all excluded by construction rather than by ordering.
/// </summary>
public static class PruneCommand
{
    public static PruneReport Execute(
        string runtimeRoot,
        bool dryRun,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        var policy = new RuntimeRetentionPolicyStore(runtimeRoot).Read();
        var lines = new List<string>();
        var reclaimed = 0L;

        // A backlog is not a sweep. The per-sweep cap keeps the broker's hot path short; a
        // deliberate prune has no such constraint and should finish in one pass.
        var archive = new DurableQueue(
                runtimeRoot,
                retention: policy with { MaximumActionsPerSweep = int.MaxValue })
            .SweepArchive(force: true, dryRun: dryRun);
        reclaimed += archive.BytesReclaimed;
        lines.Add(
            $"archive: {archive.JobsDeleted} job(s) removed, " +
            $"{archive.ResponsesDropped} response bod(ies) dropped, " +
            $"{Megabytes(archive.BytesReclaimed)}");

        foreach (var repository in Repositories(runtimeRoot))
        {
            var name = Path.GetFileName(repository);
            if (IsAbandoned(repository, policy, now))
            {
                var size = DirectorySize(repository);
                if (TryRemove(repository, dryRun))
                {
                    reclaimed += size;
                    lines.Add($"repository {name[..12]}: abandoned record removed, {Megabytes(size)}");
                }

                continue;
            }

            var result = GenerationRetention.Prune(repository, policy, now, dryRun);
            if (result.ActionCount == 0)
            {
                continue;
            }

            reclaimed += result.BytesReclaimed;
            lines.Add(
                $"repository {name[..12]}: {result.GenerationsRemoved.Count} generation(s), " +
                $"{result.OverlaysRemoved.Count} overlay set(s), " +
                $"{result.StagingRemoved.Count} stale file(s), " +
                Megabytes(result.BytesReclaimed));
        }

        var telemetry = PruneTelemetry(runtimeRoot, policy, now, dryRun);
        if (telemetry.Removed > 0)
        {
            reclaimed += telemetry.Bytes;
            lines.Add(
                $"telemetry: {telemetry.Removed} record(s) removed, {Megabytes(telemetry.Bytes)}");
        }

        var versions = PruneVersions(runtimeRoot, policy, dryRun);
        reclaimed += versions.Bytes;
        if (versions.Lines.Count > 0)
        {
            lines.AddRange(versions.Lines);
        }

        return new PruneReport(archive, lines, reclaimed);
    }

    private static IEnumerable<string> Repositories(string runtimeRoot)
    {
        var root = Path.Combine(runtimeRoot, "repositories");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            // The single-file index directory predates generations and is keyed by path, not by
            // repository identity. It has no manifest and is not a repository record.
            .Where(directory => !string.Equals(
                Path.GetFileName(directory),
                "legacy",
                StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// A repository record nothing can ever use again.
    ///
    /// Two shapes qualify: a checkout that no longer exists on disk — a throwaway worktree is the
    /// usual case — and a record that started indexing, never published a generation and has not
    /// been touched since. Both otherwise sit in the runtime forever advertising a state that will
    /// never change.
    /// </summary>
    private static bool IsAbandoned(
        string repositoryRuntimeRoot,
        RuntimeRetentionPolicy policy,
        DateTimeOffset now)
    {
        RepositoryManifest? manifest;
        try
        {
            manifest = new RepositoryManifestStore(repositoryRuntimeRoot).Read();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
                InvalidDataException)
        {
            return false;
        }

        if (manifest is null)
        {
            return false;
        }

        if (!Directory.Exists(manifest.CommonDirectory))
        {
            return true;
        }

        var generations = Path.Combine(repositoryRuntimeRoot, "generations");
        var published = Directory.Exists(generations) &&
                        Directory.EnumerateDirectories(generations).Any();
        return !published &&
               manifest.State == RepositoryIndexState.Initializing &&
               now - manifest.UpdatedAtUtc >= policy.ArchiveRetention;
    }

    /// <summary>
    /// Drops job telemetry past its retention.
    ///
    /// One record is a few hundred bytes describing one job — no prompts, no answers, no paths —
    /// and nothing ever removed them, so the directory grows for the life of the installation.
    /// The file name starts with the record's timestamp in fixed-width ticks, so age is read from
    /// the name and a file whose name does not parse is left alone rather than guessed at.
    /// </summary>
    private static (int Removed, long Bytes) PruneTelemetry(
        string runtimeRoot,
        RuntimeRetentionPolicy policy,
        DateTimeOffset now,
        bool dryRun)
    {
        var metrics = Path.Combine(runtimeRoot, "telemetry", "metrics");
        if (!Directory.Exists(metrics))
        {
            return (0, 0);
        }

        var cutoff = (now - policy.TelemetryRetention).UtcTicks;
        var removed = 0;
        var bytes = 0L;
        foreach (var path in Directory.EnumerateFiles(metrics, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var separator = name.IndexOf('-', StringComparison.Ordinal);
            if (separator <= 0 ||
                !long.TryParse(name[..separator], out var ticks) ||
                ticks >= cutoff)
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(path).Length;
                if (!dryRun)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            bytes += size;
            removed++;
        }

        return (removed, bytes);
    }

    /// <summary>
    /// Keeps the current version plus enough predecessors to roll back to, and the same number of
    /// launcher backups.
    ///
    /// Deletion is attempted rather than forced. A version whose files are still mapped by a
    /// running tool refuses to go, which is exactly the right answer — the next prune collects it
    /// once that process exits.
    /// </summary>
    private static (IReadOnlyList<string> Lines, long Bytes) PruneVersions(
        string runtimeRoot,
        RuntimeRetentionPolicy policy,
        bool dryRun)
    {
        var binRoot = Path.Combine(runtimeRoot, "bin");
        var versionsRoot = Path.Combine(binRoot, "versions");
        if (!Directory.Exists(versionsRoot))
        {
            return ([], 0);
        }

        string? current;
        try
        {
            // The shared lease is what the launcher itself holds for every tool it starts, so
            // taking another one cannot block; it only stops an installer from swapping the
            // pointer underneath this pass.
            using var lease = ActivationCoordinator.AcquireShared(binRoot);
            current = CurrentPointerSnapshot.Read(lease).Version;
        }
        catch (Exception exception) when (
            exception is ActivationCoordinationException or CurrentPointerException or
                JsonException or IOException or UnauthorizedAccessException)
        {
            return (
                ["versions: skipped, the current-version pointer could not be read safely"],
                0);
        }

        if (current is null)
        {
            return (["versions: skipped, no current version is installed"], 0);
        }

        var lines = new List<string>();
        var bytes = 0L;
        var removed = 0;
        var retained = Directory.EnumerateDirectories(versionsRoot)
            .Select(directory => new
            {
                Directory = directory,
                Name = Path.GetFileName(directory),
                InstalledAtUtc = Directory.GetLastWriteTimeUtc(directory),
            })
            .OrderByDescending(version => version.InstalledAtUtc)
            .ToArray();
        var keep = new HashSet<string>([current], StringComparer.OrdinalIgnoreCase);
        foreach (var version in retained)
        {
            if (keep.Count >= policy.InstalledVersions)
            {
                break;
            }

            keep.Add(version.Name);
        }

        foreach (var version in retained.Where(item => !keep.Contains(item.Name)))
        {
            var size = DirectorySize(version.Directory);
            if (!TryRemove(version.Directory, dryRun))
            {
                continue;
            }

            bytes += size;
            removed++;
        }

        if (removed > 0)
        {
            lines.Add($"versions: {removed} removed, {Megabytes(bytes)}");
        }

        var backupsRoot = Path.Combine(runtimeRoot, "installer", "backups");
        if (!Directory.Exists(backupsRoot))
        {
            return (lines, bytes);
        }

        var backupBytes = 0L;
        var backupsRemoved = 0;
        var backups = Directory.EnumerateDirectories(backupsRoot)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Skip(policy.LauncherBackups)
            .ToArray();
        foreach (var backup in backups)
        {
            var size = DirectorySize(backup);
            if (!TryRemove(backup, dryRun))
            {
                continue;
            }

            backupBytes += size;
            backupsRemoved++;
        }

        if (backupsRemoved > 0)
        {
            lines.Add($"installer backups: {backupsRemoved} removed, {Megabytes(backupBytes)}");
        }

        return (lines, bytes + backupBytes);
    }

    private static bool TryRemove(string path, bool dryRun)
    {
        if (dryRun)
        {
            return true;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        return 0;
                    }
                });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static string Megabytes(long bytes) =>
        $"{bytes / (1024.0 * 1024.0):F1} MB";
}
