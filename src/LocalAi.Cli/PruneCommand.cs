using CodeSearch.Core.Indexing;
using LocalAi.Broker;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Repository;
using System.Globalization;
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
        if (dryRun)
        {
            // First, not last. The rows below are counts and read the same either way, so the
            // report used to claim nine deletions and correct itself with one word in capitals
            // after all of them.
            lines.Add(CliText.PruneDryRun);
        }

        // A backlog is not a sweep. The per-sweep cap keeps the broker's hot path short; a
        // deliberate prune has no such constraint and should finish in one pass.
        var archive = new DurableQueue(
                runtimeRoot,
                retention: policy with { MaximumActionsPerSweep = int.MaxValue })
            .SweepArchive(force: true, dryRun: dryRun);
        reclaimed += archive.BytesReclaimed;
        lines.Add(CliText.PruneArchive(
            archive.JobsDeleted,
            archive.ResponsesDropped,
            Megabytes(archive.BytesReclaimed)));
        if (archive.QuarantineDeleted > 0)
        {
            lines.Add(CliText.PruneQuarantine(archive.QuarantineDeleted));
        }

        foreach (var repository in Repositories(runtimeRoot))
        {
            var name = Path.GetFileName(repository);
            if (IsAbandoned(repository, policy, now) is { } abandonment)
            {
                var size = DirectorySize(repository);
                if (TryRemove(repository, dryRun))
                {
                    reclaimed += size;
                    lines.Add(abandonment == Abandonment.CheckoutGone
                        ? CliText.PruneRecordCheckoutGone(name[..12], Megabytes(size))
                        : CliText.PruneRecordNeverIndexed(name[..12], Megabytes(size)));
                }

                continue;
            }

            var result = GenerationRetention.Prune(
                repository,
                policy,
                now,
                dryRun,
                ReachableOverlays(repository, runtimeRoot));
            if (result.ActionCount == 0)
            {
                continue;
            }

            reclaimed += result.BytesReclaimed;
            lines.Add(CliText.PruneRepositorySwept(
                name[..12],
                result.GenerationsRemoved.Count,
                result.OverlaysRemoved.Count,
                result.StagingRemoved.Count,
                Megabytes(result.BytesReclaimed)));
        }

        var telemetry = PruneTelemetry(runtimeRoot, policy, now, dryRun);
        if (telemetry.Removed > 0)
        {
            reclaimed += telemetry.Bytes;
            lines.Add(CliText.PruneTelemetry(telemetry.Removed, Megabytes(telemetry.Bytes)));
        }

        var versions = PruneVersions(runtimeRoot, policy, dryRun);
        reclaimed += versions.Bytes;
        if (versions.Lines.Count > 0)
        {
            lines.AddRange(versions.Lines);
        }

        return new PruneReport(archive, lines, reclaimed);
    }

    /// <summary>
    /// What each repository can still be asked about: every worktree its manifest records that
    /// still exists, keyed the way its overlay directory is, with the tree its HEAD is on.
    ///
    /// Without this the command the documentation names for reclaiming space was the one path
    /// that could not reclaim the bulk of it: overlays under a kept generation are most of what
    /// accumulates, and passing nothing meant leaving all of them alone.
    ///
    /// Null on any doubt — an unreadable manifest, a worktree that cannot be inspected, a
    /// manifest recording no worktrees at all. The sweep then leaves overlays alone for this
    /// repository, which costs disk until the next sync; the other way costs a worktree its
    /// index until somebody notices.
    /// </summary>
    private static IReadOnlySet<(string WorktreeId, string HeadTree)>? ReachableOverlays(
        string repositoryRuntimeRoot,
        string runtimeRoot)
    {
        try
        {
            var manifest = new RepositoryManifestStore(FsPath.From(repositoryRuntimeRoot)).Read();
            if (manifest is null)
            {
                return null;
            }

            var reachable = new HashSet<(string, string)>();
            foreach (var worktree in manifest.ActiveWorktrees)
            {
                if (IsGone(worktree.Path))
                {
                    // Recorded but gone: its overlays are exactly what this is here to collect.
                    continue;
                }

                // Only the working root and the head tree are wanted. Inspect would also list
                // the dirty paths and hash the full text of every one of them — six git calls
                // and a read of the whole working diff, per worktree, on a pass that is
                // otherwise directory enumeration and runs under --dry-run too.
                var workingRoot = RepoLocator.ResolveWorkingRoot(worktree.Path);
                reachable.Add((
                    RuntimeIndexLayout.WorktreeKey(workingRoot),
                    RepoLocator.GitOutputOrThrow(
                        workingRoot.Value,
                        "rev-parse HEAD^{tree}",
                        "The git HEAD tree")));
            }

            // An empty set is not the same answer as "leave these alone": retention reads it as
            // nothing being reachable and removes every overlay under every kept generation. A
            // manifest that records no worktrees is a manifest that cannot answer the question.
            return reachable.Count == 0 ? null : reachable;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // InvalidDataException is what a corrupt manifest raises — an unsupported schema, a
            // checksum that does not decode or does not match. Leaving it out did not merely
            // skip this repository: it escaped Execute and ended the whole run, so telemetry,
            // installed versions and launcher backups were never swept.
            Console.Error.WriteLine(CliText.PruneOverlaysLeftAlone(
                Path.GetFileName(repositoryRuntimeRoot)[..12],
                exception.Message));
            return null;
        }
    }

    /// <summary>
    /// Whether a recorded worktree is really gone, as opposed to merely unreachable right now.
    ///
    /// <see cref="Directory.Exists(string)"/> answers false for both, without saying which — an
    /// unmounted volume, a disconnected share and a subst drive absent from this session all
    /// look exactly like a deleted checkout. Reading that as "deleted" would collect the
    /// overlays of every worktree on a drive that happens to be offline. So the volume is asked
    /// about first: a missing directory on a root that is present is a deletion, and anything
    /// else is doubt, which the caller turns into leaving this repository's overlays alone.
    /// </summary>
    private static bool IsGone(string worktreePath)
    {
        if (Directory.Exists(worktreePath))
        {
            return false;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(worktreePath));
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            throw new IOException(CliText.PruneVolumeUnavailable(worktreePath));
        }

        return true;
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
    /// <summary>
    /// Which of the two shapes was matched, or null for a record that is neither.
    ///
    /// They are unrelated, and the report used to print one sentence for both. Only the cause
    /// tells a reader whether a deletion was right: a checkout that is gone is expected, while a
    /// record that never finished indexing may be one somebody is still waiting on.
    /// </summary>
    private enum Abandonment
    {
        CheckoutGone,
        NeverIndexed,
    }

    private static Abandonment? IsAbandoned(
        string repositoryRuntimeRoot,
        RuntimeRetentionPolicy policy,
        DateTimeOffset now)
    {
        RepositoryManifest? manifest;
        try
        {
            manifest = new RepositoryManifestStore(FsPath.From(repositoryRuntimeRoot)).Read();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
                InvalidDataException)
        {
            return null;
        }

        if (manifest is null)
        {
            return null;
        }

        // Through IsGone rather than Directory.Exists, for the reason IsGone was written: an
        // unmounted volume, a disconnected share and an absent subst drive all answer false, and
        // reading that as "deleted" here costs the whole record — its manifest and every
        // generation under it. The overlay pass has always asked the volume first; this check
        // deleted far more on the same evidence.
        try
        {
            if (IsGone(manifest.CommonDirectory))
            {
                return Abandonment.CheckoutGone;
            }
        }
        catch (IOException)
        {
            // The volume could not be asked about. Doubt leaves the record alone: keeping an
            // abandoned one costs disk until the next prune, and removing a live one costs hours
            // of embedding.
            return null;
        }

        var generations = Path.Combine(repositoryRuntimeRoot, "generations");
        var published = Directory.Exists(generations) &&
                        Directory.EnumerateDirectories(generations).Any();
        return !published &&
               manifest.State == RepositoryIndexState.Initializing &&
               now - manifest.UpdatedAtUtc >= policy.ArchiveRetention
            ? Abandonment.NeverIndexed
            : null;
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
            return ([CliText.PruneVersionsPointerUnreadable(exception.Message)], 0);
        }

        if (current is null)
        {
            return ([CliText.PruneVersionsNoPointer], 0);
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
            lines.Add(CliText.PruneVersions(removed, Megabytes(bytes)));
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
            lines.Add(CliText.PruneLauncherBackups(backupsRemoved, Megabytes(backupBytes)));
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

    /// <summary>
    /// The number alone, invariantly. The unit belongs to whichever sentence prints it, because
    /// `localai sync` already writes it in the reader's language and the two commands must not
    /// disagree about it on the same machine in the same minute.
    /// </summary>
    internal static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
}
