using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalAi.Repository;
using System.Text;

namespace LocalAi.Cli;

public sealed record CodeSearchSyncResult(
    string RepositoryId,
    string GenerationId,
    string BaseIndexPath,
    int OverlaysBuilt,
    bool GenerationChanged,
    // Reported rather than counted silently: fewer overlays than worktrees is otherwise
    // indistinguishable from a sync that decided they were all up to date.
    int WorktreesSkipped = 0);

public static class CodeSearchSyncCommand
{
    public const string DefaultModel = "qwen3-embedding:8b-q8_0";
    public const int DefaultDimension = 4096;
    public const int CurrentNormalizationVersion = 4;

    public static async Task<CodeSearchSyncResult> ExecuteAsync(
        string workingRoot,
        string model = DefaultModel,
        CancellationToken cancellationToken = default)
    {
        var requested = RuntimeIndexLayout.Inspect(workingRoot);
        var progressStore = new RepositoryIndexProgressStore(
            requested.RepositoryRuntimeRoot);
        var manifestStore = new RepositoryManifestStore(
            requested.RepositoryRuntimeRoot);
        var lastProgress = new RepositoryIndexProgress(
            requested.RepositoryId,
            RepositoryIndexProgressPhase.Planning,
            requested.WorkingRoot,
            0,
            0,
            0,
            null,
            DateTimeOffset.UtcNow);
        progressStore.Save(lastProgress);

        void ReportProgress(
            RepositoryIndexProgressPhase phase,
            string root,
            IndexBuildProgress progress)
        {
            lastProgress = new RepositoryIndexProgress(
                requested.RepositoryId,
                phase,
                root,
                progress.ProcessedChunks,
                progress.TotalChunks,
                progress.ChunksPerSecond,
                progress.EstimatedRemaining,
                DateTimeOffset.UtcNow);
            progressStore.Save(lastProgress);
        }

        try
        {
            var worktrees = ReadWorktrees(requested.WorkingRoot);
            var mainline = ResolveMainline(
                requested,
                manifestStore.Read()?.DevRef);
            var generationIdentity = new GenerationIdentity(
                requested.RepositoryId,
                mainline.Identity.HeadCommit,
                mainline.Identity.HeadTree,
                model,
                DefaultDimension,
                1,
                CodeIndex.CurrentVersion,
                CurrentNormalizationVersion,
                1);
            var commonDirectory = RepositoryIdentity.FromCommonDirectory(
                RepoLocator.GitOutput(
                    requested.WorkingRoot,
                    "rev-parse --path-format=absolute --git-common-dir")
                ?? throw new InvalidOperationException("Git common directory is unavailable."))
                .CommonDirectory;
            manifestStore.Save(new RepositoryManifest(
                requested.RepositoryId,
                commonDirectory,
                mainline.Ref,
                null,
                null,
                model,
                DefaultDimension,
                1,
                CodeIndex.CurrentVersion,
                RepositoryIndexState.Initializing,
                worktrees
                    .Where(item => !item.IsPrunable)
                    .Select(item => new RepositoryWorktree(item.Path, item.Head, item.Branch))
                    .ToArray(),
                DateTimeOffset.UtcNow));
            var store = new GenerationStore(requested.RepositoryRuntimeRoot);
            var current = store.ReadCurrent();
            var generationChanged = !string.Equals(
                current?.GenerationId,
                generationIdentity.Id,
                StringComparison.Ordinal);

            GenerationManifest generation;
            try
            {
                generation = store.ReadManifest(generationIdentity.Id);
            }
            catch (Exception error) when (
                error is DirectoryNotFoundException or FileNotFoundException)
            {
                generation = await BuildGenerationAsync(
                    store,
                    current,
                    generationIdentity,
                    mainline.Identity,
                    progress => ReportProgress(
                        RepositoryIndexProgressPhase.EmbeddingBase,
                        mainline.Identity.WorkingRoot,
                        progress),
                    cancellationToken);
            }

            var targets = generationChanged
                ? worktrees
                : worktrees.Where(
                    item => SamePath(item.Path, requested.WorkingRoot)).ToArray();
            var overlaysBuilt = 0;
            var present = SelectPresentWorktrees(
                targets.Where(item => !item.IsPrunable),
                path => Console.Error.WriteLine(
                    $"Worktree {path} no longer exists; skipping its overlay."));
            var worktreesSkipped = present.Skipped;
            foreach (var worktree in present.Worktrees)
            {
                WorkingIndexIdentity identity;
                try
                {
                    identity = RuntimeIndexLayout.Inspect(worktree.Path);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or IOException or
                        UnauthorizedAccessException &&
                    !Directory.Exists(worktree.Path))
                {
                    Console.Error.WriteLine(
                        $"Worktree {worktree.Path} disappeared while it was being inspected; " +
                        "skipping its overlay.");
                    worktreesSkipped++;
                    continue;
                }

                if (string.Equals(
                        identity.HeadTree,
                        generation.Identity.DevTree,
                        StringComparison.Ordinal) &&
                    identity.DirtyHash is null)
                {
                    continue;
                }

                var overlayPath = RuntimeIndexLayout.OverlayPath(
                    identity,
                    generation.Identity.Id);
                if (File.Exists(overlayPath))
                {
                    continue;
                }

                var embedder = new BrokerEmbeddingClient(
                    generation.Identity.EmbeddingModel,
                    BrokerClientFactory.CreateDefault());
                var builder = new IndexBuilder(
                    embedder,
                    Console.Error.WriteLine,
                    progress => ReportProgress(
                        RepositoryIndexProgressPhase.EmbeddingOverlay,
                        identity.WorkingRoot,
                        progress));
                await builder.BuildOverlayAsync(
                    identity.WorkingRoot,
                    store.IndexPath(generation.Identity.Id),
                    overlayPath,
                    cancellationToken,
                    new IndexBuildContext(
                        identity.WorkingRoot,
                        identity.HeadCommit,
                        identity.HeadTree,
                        identity.RepositoryId,
                        generation.Identity.Id,
                        identity.DirtyHash));
                overlaysBuilt++;
            }

            progressStore.Save(lastProgress = lastProgress with
            {
                Phase = RepositoryIndexProgressPhase.Publishing,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            store.SetCurrent(generation);
            var manifest = new RepositoryManifest(
                requested.RepositoryId,
                commonDirectory,
                mainline.Ref,
                generation.Identity.Id,
                generation.Identity.DevTree,
                generation.Identity.EmbeddingModel,
                generation.Identity.EmbeddingDimension,
                generation.Identity.ChunkFormatVersion,
                generation.Identity.IndexFormatVersion,
                RepositoryIndexState.Current,
                worktrees
                    .Where(item => !item.IsPrunable)
                    .Select(item => new RepositoryWorktree(
                        item.Path,
                        item.Head,
                        item.Branch))
                    .ToArray(),
                DateTimeOffset.UtcNow);
            manifestStore.Save(manifest);

            progressStore.Save(lastProgress = lastProgress with
            {
                Phase = RepositoryIndexProgressPhase.Completed,
                ProcessedChunks = lastProgress.TotalChunks,
                EstimatedRemaining = TimeSpan.Zero,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            return new CodeSearchSyncResult(
                requested.RepositoryId,
                generation.Identity.Id,
                store.IndexPath(generation.Identity.Id),
                overlaysBuilt,
                generationChanged,
                worktreesSkipped);
        }
        catch
        {
            try
            {
                progressStore.Save(lastProgress with
                {
                    Phase = RepositoryIndexProgressPhase.Failed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
            catch (Exception progressError) when (
                progressError is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException)
            {
                // Progress is observational and cannot replace the indexing failure.
            }

            throw;
        }
    }

    private static Mainline ResolveMainline(
        WorkingIndexIdentity requested,
        string? configuredRef)
    {
        var candidates = new[]
            {
                configuredRef,
                "refs/heads/dev",
                "refs/heads/main"
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var commit = GitValue(
                requested.WorkingRoot,
                "rev-parse",
                "--verify",
                $"{candidate}^{{commit}}");
            var tree = GitValue(
                requested.WorkingRoot,
                "rev-parse",
                "--verify",
                $"{candidate}^{{tree}}");
            if (commit is null || tree is null)
            {
                continue;
            }

            return new Mainline(
                candidate!,
                requested with
                {
                    HeadCommit = commit,
                    HeadTree = tree,
                    DirtyHash = null
                });
        }

        throw new InvalidOperationException(
            "A local mainline branch is required before indexing. " +
            "Expected refs/heads/dev or refs/heads/main.");
    }

    private static async Task<GenerationManifest> BuildGenerationAsync(
        GenerationStore store,
        GenerationPointer? current,
        GenerationIdentity generation,
        WorkingIndexIdentity dev,
        Action<IndexBuildProgress> progress,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(
            dev.RepositoryRuntimeRoot,
            "staging");
        Directory.CreateDirectory(stagingRoot);
        var workIndex = Path.Combine(
            stagingRoot,
            generation.Id + "." + Guid.NewGuid().ToString("N") + ".cidx");
        try
        {
            if (current is not null)
            {
                var previous = store.ReadManifest(current.GenerationId);
                var reusable = store.IndexPath(previous.Identity.Id);
                if (generation.CanReuseCorpusFrom(previous.Identity) &&
                    File.Exists(reusable))
                {
                    File.Copy(reusable, workIndex);
                }
            }

            using var snapshot = CommitSnapshot.Create(dev.WorkingRoot, dev.HeadCommit);
            var embedder = new BrokerEmbeddingClient(
                generation.EmbeddingModel,
                BrokerClientFactory.CreateDefault());
            var builder = new IndexBuilder(
                embedder,
                Console.Error.WriteLine,
                progress);
            await builder.BuildAsync(
                snapshot.Root,
                workIndex,
                force: false,
                cancellationToken,
                new IndexBuildContext(
                    dev.WorkingRoot,
                    dev.HeadCommit,
                    dev.HeadTree,
                    dev.RepositoryId,
                    generation.Id));
            var header = CodeIndex.Load(workIndex, withVectors: false);
            if (header.Dim != generation.EmbeddingDimension)
            {
                throw new InvalidDataException(
                    $"Embedding dimension {header.Dim} does not match expected " +
                    $"{generation.EmbeddingDimension}.");
            }

            return store.PublishIndex(workIndex, generation);
        }
        finally
        {
            if (File.Exists(workIndex))
            {
                File.Delete(workIndex);
            }
        }
    }

    /// <summary>
    /// Drops the worktrees that are no longer on disk, and says which.
    ///
    /// A worktree can be removed while this runs — it belongs to somebody else, and a sync of a
    /// large repository takes tens of minutes. Losing the whole run to that, after an hour of
    /// embedding, is the wrong answer: that overlay is gone either way, and every other
    /// worktree still deserves its own.
    ///
    /// Only absence is tolerated. A worktree that exists but cannot be read still stops the
    /// run, because publishing a generation with overlays quietly missing is worse than
    /// failing.
    /// </summary>
    internal static (IReadOnlyList<GitWorktree> Worktrees, int Skipped) SelectPresentWorktrees(
        IEnumerable<GitWorktree> targets,
        Action<string> report)
    {
        var present = new List<GitWorktree>();
        var skipped = 0;
        foreach (var worktree in targets)
        {
            if (Directory.Exists(worktree.Path))
            {
                present.Add(worktree);
                continue;
            }

            report(worktree.Path);
            skipped++;
        }

        return (present, skipped);
    }

    private static IReadOnlyList<GitWorktree> ReadWorktrees(string root)
    {
        var output = RepoLocator.GitOutput(root, "worktree list --porcelain")
            ?? throw new InvalidOperationException("Git worktree inventory is unavailable.");
        return WorktreeInventory.ParsePorcelain(output);
    }

    private static string? GitValue(string root, params string[] arguments)
    {
        var output = RepoLocator.GitOutputBytes(root, arguments);
        return output is null
            ? null
            : Encoding.UTF8.GetString(output).Trim();
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed record Mainline(
        string Ref,
        WorkingIndexIdentity Identity);
}
