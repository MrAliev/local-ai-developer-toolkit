using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Broker.Client;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Indexing;
using LocalAi.Repository;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LocalAi.Cli;

/// <summary>
/// Another sync already holds this repository's gate. A named outcome, not a failure: the
/// other run is doing the same work, its phase and ETA are in the progress file, and the
/// caller's next commit will sync again anyway. Hooks and MCP callers must not queue for
/// the minutes a full generation build can take.
/// </summary>
public sealed class RepositorySyncBusyException(string repositoryId) : Exception(
    CliText.SyncBusy(repositoryId))
{
    public string RepositoryId { get; } = repositoryId;
}

public sealed record CodeSearchSyncResult(
    string RepositoryId,
    string GenerationId,
    string BaseIndexPath,
    int OverlaysBuilt,
    bool GenerationChanged,
    // Reported rather than counted silently: fewer overlays than worktrees is otherwise
    // indistinguishable from a sync that decided they were all up to date.
    int WorktreesSkipped = 0,
    // Non-null when the run refused rather than built: the work was over the caller's inline
    // limit, and the number is the files it would have had to re-read.
    int? RefusedFiles = null);

public static class CodeSearchSyncCommand
{
    public const string DefaultModel = "qwen3-embedding:8b-q8_0";
    public const int DefaultDimension = 4096;
    public const int CurrentNormalizationVersion = 4;

    /// <summary>
    /// Version 2 cuts adapter-covered files on their definitions instead of on a line window.
    /// Version 3 also cuts on the declarations an adapter names without reporting a body span.
    /// </summary>
    /// <remarks>
    /// Chunk boundaries are part of the generation identity, so this is not a migration: the
    /// first sync after the upgrade rebuilds every repository's base generation from scratch.
    /// For a corpus the size of IntelWash that is roughly 26 000 chunks, on the order of an hour
    /// at the rate this machine embeds — a deliberate rebuild, announced in the release notes,
    /// and it must read as one in `index_status` rather than as drift.
    /// </remarks>
    public const int CurrentChunkFormatVersion = 3;

    // Bump whenever semantic extraction changes even if the SIDX binary format does not.
    // Generations are immutable, so changing relationships without changing this value
    // would keep serving the previous semantic graph for an already indexed commit.
    public const int CurrentSemanticGenerationVersion = 10;

    // Long enough to ride out another run's short publish transaction, far too short to
    // queue behind its embedding phase: a blocked caller exits with the named busy outcome.
    private static readonly TimeSpan SyncGateWaitBudget = TimeSpan.FromSeconds(5);

    internal sealed record SemanticBuildResult(
        SemanticIndex Index,
        IReadOnlyList<SemanticAdapterStatus> AdapterStatuses,
        IReadOnlyList<string>? UncoveredProjects = null);

    /// <param name="runtimeRoot">
    /// The installation this sync publishes into and reads its policy from. Null means the
    /// machine's own, which is the only value production uses; naming one is what lets a test
    /// run a whole sync without touching the real runtime.
    /// </param>
    public static async Task<CodeSearchSyncResult> ExecuteAsync(
        string workingRoot,
        string model = DefaultModel,
        CancellationToken cancellationToken = default,
        bool includeOverlays = true,
        string? runtimeRoot = null,
        bool requireSemantics = false,
        int? refuseInlineOverFiles = null)
    {
        var requested = RuntimeIndexLayout.Inspect(workingRoot, runtimeRoot);
        // The gate comes before the first write of any shared state — progress included.
        // Everything below (progress, manifest, generation directories, checkpoints, the
        // current pointer, prune) is single-writer while the lease is held, which is what
        // lets the failure path stamp Failed without asking whose progress it stamps (#199).
        using var syncLease = RepositorySyncGate.TryAcquire(
            requested.RepositoryId,
            SyncGateWaitBudget,
            cancellationToken)
            ?? throw new RepositorySyncBusyException(requested.RepositoryId);
        var progressStore = new RepositoryIndexProgressStore(
            requested.RepositoryRuntimeRoot);
        var manifestStore = new RepositoryManifestStore(
            requested.RepositoryRuntimeRoot);
        var lastProgress = new RepositoryIndexProgress(
            requested.RepositoryId,
            RepositoryIndexProgressPhase.Planning,
            requested.WorkingRoot.Value,
            0,
            0,
            0,
            null,
            DateTimeOffset.UtcNow);
        progressStore.Save(lastProgress);

        // Before the lease does any work, and long before the semantic phase: this is the only
        // point where declining is free. Scanning and hashing the tree costs seconds and needs
        // no model; everything after it — Roslyn loading the solution, the SCIP adapters, the
        // embedding — is what a bounded caller cannot afford to have started.
        //
        // Counted in files because chunk counts do not exist yet: C# is cut on the definitions
        // the semantic phase produces, so asking for them here would mean paying for it.
        if (refuseInlineOverFiles is { } fileLimit)
        {
            var currentBase = new GenerationStore(requested.RepositoryRuntimeRoot).ReadCurrent();
            var changedFiles = IndexBuilder.CountChangedFiles(
                requested.WorkingRoot.Value,
                currentBase is null
                    ? null
                    : new GenerationStore(requested.RepositoryRuntimeRoot)
                        .IndexPath(currentBase.GenerationId));
            if (changedFiles > fileLimit)
            {
                return new CodeSearchSyncResult(
                    requested.RepositoryId,
                    string.Empty,
                    string.Empty,
                    0,
                    false,
                    0,
                    changedFiles);
            }
        }

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

        // Phases that do not count chunks still have to be announced, or the last embedding
        // report stands as the whole story for as long as they run. The counters are cleared with
        // the phase rather than carried into it: a stale "1415/1415" is worse than no number.
        void ReportPhase(RepositoryIndexProgressPhase phase, string root)
        {
            lastProgress = lastProgress with
            {
                Phase = phase,
                WorkingRoot = root,
                ProcessedChunks = 0,
                TotalChunks = 0,
                ChunksPerSecond = 0,
                EstimatedRemaining = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            progressStore.Save(lastProgress);
        }

        try
        {
            var worktrees = ReadWorktrees(requested.WorkingRoot.Value);
            var mainline = ResolveMainline(
                requested,
                manifestStore.Read()?.DevRef);
            var generationIdentity = new GenerationIdentity(
                requested.RepositoryId,
                mainline.Identity.HeadCommit,
                mainline.Identity.HeadTree,
                model,
                DefaultDimension,
                CurrentChunkFormatVersion,
                CodeIndex.CurrentVersion,
                CurrentNormalizationVersion,
                1,
                CurrentSemanticGenerationVersion);
            var commonDirectory = RepositoryIdentity.FromCommonDirectory(
                RepoLocator.GitOutputOrThrow(
                    requested.WorkingRoot.Value,
                    "rev-parse --path-format=absolute --git-common-dir",
                    "The git common directory"))
                .CommonDirectory;
            manifestStore.Save(new RepositoryManifest(
                requested.RepositoryId,
                commonDirectory,
                mainline.Ref,
                null,
                null,
                model,
                DefaultDimension,
                CurrentChunkFormatVersion,
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
                        mainline.Identity.WorkingRoot.Value,
                        progress),
                    phase => ReportPhase(phase, mainline.Identity.WorkingRoot.Value),
                    runtimeRoot,
                    requireSemantics,
                    cancellationToken);
            }

            var targets = includeOverlays
                ? generationChanged
                    ? worktrees
                    : worktrees.Where(
                        item => FsPath.From(item.Path) == requested.WorkingRoot).ToArray()
                : [];
            var overlaysBuilt = 0;
            var present = SelectPresentWorktrees(
                targets.Where(item => !item.IsPrunable),
                path => Console.Error.WriteLine(CliText.WorktreeGone(path)));
            var worktreesSkipped = present.Skipped;
            foreach (var worktree in present.Worktrees)
            {
                WorkingIndexIdentity identity;
                try
                {
                    identity = RuntimeIndexLayout.Inspect(worktree.Path, runtimeRoot);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or IOException or
                        UnauthorizedAccessException &&
                    !Directory.Exists(worktree.Path))
                {
                    Console.Error.WriteLine(
                        CliText.WorktreeVanished(worktree.Path));
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
                var semanticOverlayPath = RuntimeIndexLayout.SemanticOverlayPath(
                    identity,
                    generation.Identity.Id);
                var builtOverlay = false;

                // Semantics first here too, and for the second of the two reasons the base
                // generation does it: a file changed on a branch has to be cut on the same
                // boundaries as the same file in the base generation. Built the other way round,
                // a branch would re-chunk by line window everything it touched, and the shape of
                // a hit would depend on whether the file happened to be in an overlay.
                SemanticIndex? worktreeSemantics = null;
                if (!IsCurrentSemanticOverlay(
                        semanticOverlayPath,
                        identity,
                        generation.Identity))
                {
                    ReportPhase(
                        RepositoryIndexProgressPhase.SemanticOverlay,
                        identity.WorkingRoot.Value);
                    var semanticBuild = await BuildSemanticIndexAsync(
                        identity.WorkingRoot.Value,
                        identity,
                        generation.Identity,
                        runtimeRoot,
                        cancellationToken);

                    // A failed adapter aborts the base generation and deliberately does not abort
                    // this. The base is immutable: boundaries cut by the window because an
                    // indexer was broken would be frozen into a generation that is never rebuilt
                    // while the tree stands still. An overlay is not — it is rebuilt whenever the
                    // branch moves or a file changes, so the degradation lasts as long as the
                    // breakage does. And the alternative is worse in both directions: refusing
                    // the overlay fails the post-commit hook on every commit until someone
                    // repairs a Node package, and answering from the base alone would answer
                    // about code the branch has already changed.
                    //
                    // It is a degradation, not a non-event, so it is said out loud rather than
                    // left to be inferred from the adapter line above.
                    WarnDegradedSemanticOverlay(
                        semanticBuild.AdapterStatuses,
                        identity.WorkingRoot.Value);
                    var baseSemanticIndex = SemanticIndex.Load(
                        store.SemanticIndexPath(generation.Identity.Id));
                    var semanticOverlay = SemanticIndexOverlay.Create(
                        baseSemanticIndex,
                        semanticBuild.Index,
                        RuntimeIndexLayout.GetDirtyPaths(identity.WorkingRoot.Value));
                    semanticOverlay.Save(semanticOverlayPath);
                    worktreeSemantics = semanticBuild.Index;
                    builtOverlay = true;
                }

                if (!File.Exists(overlayPath))
                {
                    // Only now, and only if the corpus overlay is actually being built: an
                    // up-to-date semantic overlay still has to be materialised against the base
                    // index to answer "what are this worktree's definitions", and that reads the
                    // whole base SIDX. Doing it unconditionally would put that cost on every
                    // sync that has nothing to do.
                    var definitions = SymbolDefinitionCatalog.FromSemanticIndex(
                        worktreeSemantics ?? SemanticIndexOverlay
                            .Load(semanticOverlayPath)
                            .Materialize(SemanticIndex.Load(
                                store.SemanticIndexPath(generation.Identity.Id))));
                    var embeddingCheckpointPath = overlayPath + ".embedding-checkpoint";
                    var embedder = new BrokerEmbeddingClient(
                        generation.Identity.EmbeddingModel,
                        BrokerClientFactory.CreateDefault());
                    var builder = new IndexBuilder(
                        embedder,
                        Console.Error.WriteLine,
                        progress => ReportProgress(
                            RepositoryIndexProgressPhase.EmbeddingOverlay,
                            identity.WorkingRoot.Value,
                            progress),
                        definitions);
                    await builder.BuildOverlayAsync(
                        identity.WorkingRoot.Value,
                        store.IndexPath(generation.Identity.Id),
                        overlayPath,
                        cancellationToken,
                        new IndexBuildContext(
                            identity.WorkingRoot.Value,
                            identity.HeadCommit,
                            identity.HeadTree,
                            identity.RepositoryId,
                            generation.Identity.Id,
                            identity.DirtyHash),
                        embeddingCheckpointPath,
                        generation.Identity.EmbeddingDimension);
                    DeleteEmbeddingCheckpoint(embeddingCheckpointPath);
                    builtOverlay = true;
                }

                if (builtOverlay)
                {
                    if (DiscardDriftedOverlay(
                            identity,
                            overlayPath,
                            semanticOverlayPath,
                            runtimeRoot))
                    {
                        worktreesSkipped++;
                    }
                    else
                    {
                        overlaysBuilt++;
                    }
                }
            }

            ReportPhase(RepositoryIndexProgressPhase.Publishing, requested.WorkingRoot.Value);
            store.SetCurrent(generation, current);
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
            PruneSupersededGenerations(
                requested.RepositoryRuntimeRoot.Value,
                runtimeRoot,
                ReachableOverlays(worktrees, runtimeRoot));

            progressStore.Save(lastProgress = lastProgress with
            {
                Phase = RepositoryIndexProgressPhase.Completed,
                ProcessedChunks = lastProgress.TotalChunks,
                EstimatedRemaining = TimeSpan.Zero,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
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

    /// <summary>
    /// The identity check overlays owe their name to (#197).
    ///
    /// The base generation is immune to edits during its build: it reads a materialized
    /// CommitSnapshot, never the live worktree. Overlays are the opposite — they read the
    /// working tree for minutes and land at final paths derived from the identity captured
    /// before the build, dirty hash included. An edit landing mid-build produced a mixed
    /// overlay whose name promised an exact snapshot: the moment the worktree returned to
    /// that state, search answered confidently with content the state never contained.
    ///
    /// Re-inspecting after the build and discarding on drift closes that at the cost of an
    /// honest retry — the next sync, which the edit's own hook already scheduled, rebuilds
    /// from the new state. A worktree that vanished mid-build counts as drifted: its
    /// artifacts are unnamed-state artifacts either way.
    /// </summary>
    internal static bool DiscardDriftedOverlay(
        WorkingIndexIdentity captured,
        string overlayPath,
        string semanticOverlayPath,
        string? runtimeRoot)
    {
        var drifted = false;
        try
        {
            var live = RuntimeIndexLayout.Inspect(captured.WorkingRoot.Value, runtimeRoot);
            drifted =
                !string.Equals(live.HeadCommit, captured.HeadCommit, StringComparison.Ordinal) ||
                !string.Equals(live.HeadTree, captured.HeadTree, StringComparison.Ordinal) ||
                !string.Equals(live.DirtyHash, captured.DirtyHash, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            drifted = true;
        }

        if (!drifted)
        {
            return false;
        }

        foreach (var artifact in new[]
                 {
                     overlayPath,
                     semanticOverlayPath,
                     overlayPath + ".embedding-checkpoint",
                 })
        {
            if (File.Exists(artifact))
            {
                File.Delete(artifact);
            }
        }

        Console.Error.WriteLine(
            CliText.OverlayDiscarded(captured.WorkingRoot.Value));
        return true;
    }

    /// <summary>
    /// Drops generations this repository has outgrown, immediately after publishing the one that
    /// superseded them.
    ///
    /// Publishing a new generation is the exact moment an old one stops being reachable, and it
    /// is the only moment the count can exceed the retention bound — so this is where the bound
    /// belongs. Left to <c>localai prune</c> alone it was invisible: superseded generations and
    /// their overlays reached several hundred megabytes on a repository that had simply been
    /// committed to for a while.
    ///
    /// Scoped to this repository and to generations only. Installed versions and installer
    /// backups are the larger share of a grown runtime, and they stay with the explicit command
    /// on purpose: deleting binaries as a side effect of indexing is a worse surprise than a
    /// large directory.
    ///
    /// Never fails the sync. The index is published and correct by this point; a retention sweep
    /// that could not run is worth reporting and nothing more.
    /// </summary>
    /// <summary>
    /// What the repository can still ask about: every live worktree, keyed the way its overlay
    /// directory is, paired with the tree its HEAD points at. Anything else under a kept
    /// generation belongs to a commit nobody is on or a worktree that is gone.
    /// </summary>
    private static IReadOnlySet<(string WorktreeId, string HeadTree)>? ReachableOverlays(
        IReadOnlyList<GitWorktree> worktrees,
        string? runtimeRoot)
    {
        var reachable = new HashSet<(string, string)>();
        foreach (var worktree in worktrees.Where(item => !item.IsPrunable))
        {
            try
            {
                var identity = RuntimeIndexLayout.Inspect(worktree.Path, runtimeRoot);
                reachable.Add((
                    RuntimeIndexLayout.WorktreeKey(identity.WorkingRoot),
                    identity.HeadTree));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or
                    UnauthorizedAccessException)
            {
                // One worktree that cannot be inspected makes the whole set incomplete, and an
                // incomplete set reads as "these are gone". Inspect hashes the dirty working
                // files, so the throw lands exactly on a worktree with uncommitted work — the
                // one whose overlay matters most. Abandoning the sweep costs disk until the
                // next sync; guessing costs that worktree its index.
                Console.Error.WriteLine(CliText.WorktreeNotInspectable(
                    worktree.Path,
                    exception.Message));
                return null;
            }
        }

        return reachable;
    }

    internal static void PruneSupersededGenerations(
        string repositoryRuntimeRoot,
        string? runtimeRoot,
        IReadOnlySet<(string WorktreeId, string HeadTree)>? reachable = null)
    {
        try
        {
            var policy = new RuntimeRetentionPolicyStore(
                string.IsNullOrWhiteSpace(runtimeRoot)
                    ? RuntimeRetentionPolicyStore.DefaultRuntimeRoot
                    : runtimeRoot).Read();
            var result = GenerationRetention.Prune(
                repositoryRuntimeRoot,
                policy,
                DateTimeOffset.UtcNow,
                reachable: reachable);
            if (result.ActionCount > 0)
            {
                Console.Error.WriteLine(CliText.RetentionRemoved(
                    result.GenerationsRemoved.Count,
                    result.OverlaysRemoved.Count,
                    result.StagingRemoved.Count,
                    // One decimal, invariantly: integer division printed "0 MB" for every sweep
                    // under a megabyte, which reads as a contradiction beside a non-zero count.
                    (result.BytesReclaimed / 1024.0 / 1024.0)
                        .ToString("F1", CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(
                CliText.RetentionSweepSkipped(exception.Message));
        }
    }

    private static Mainline ResolveMainline(
        WorkingIndexIdentity requested,
        string? configuredRef)
    {
        // Materialised, so the message below names the refs this loop actually tried. It used
        // to name dev and main from a literal, which is wrong for a repository whose manifest
        // configures neither.
        var candidates = new[]
            {
                configuredRef,
                "refs/heads/dev",
                "refs/heads/main"
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in candidates)
        {
            var commit = GitValue(
                requested.WorkingRoot.Value,
                "rev-parse",
                "--verify",
                $"{candidate}^{{commit}}");
            var tree = GitValue(
                requested.WorkingRoot.Value,
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
            CliText.MainlineMissing(string.Join(", ", candidates)));
    }

    private static async Task<GenerationManifest> BuildGenerationAsync(
        GenerationStore store,
        GenerationPointer? current,
        GenerationIdentity generation,
        WorkingIndexIdentity dev,
        Action<IndexBuildProgress> progress,
        Action<RepositoryIndexProgressPhase> phase,
        string? runtimeRoot,
        bool requireSemantics,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(
            dev.RepositoryRuntimeRoot.Value,
            "staging");
        Directory.CreateDirectory(stagingRoot);
        var workIndex = Path.Combine(
            stagingRoot,
            generation.Id + "." + Guid.NewGuid().ToString("N") + ".cidx");
        // Named for the generation and nothing else, because it has to be findable by the run
        // that resumes this one. The corpus file keeps its per-run suffix: it is rebuilt from the
        // embedding checkpoint, not reused as a file.
        var workSemanticIndex = Path.Combine(
            stagingRoot,
            generation.Id + ".sidx");
        var semanticAdapterStatusPath = workSemanticIndex + ".adapters.json";
        var embeddingCheckpointPath = Path.Combine(
            stagingRoot,
            generation.Id + ".embedding-checkpoint");
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

            using var snapshot = CommitSnapshot.Create(dev.WorkingRoot.Value, dev.HeadCommit);

            // Semantics first, deliberately. Roslyn loads the whole solution here and the SCIP
            // adapters shell out per language, which is minutes on a large repository — but
            // embedding is tens of minutes, and a failed adapter aborts the generation outright.
            // Running this second meant paying for the entire corpus before finding out the build
            // could not be published, which is the wrong order to learn that in.
            //
            // An interrupted build resumes both phases: the embedding checkpoint for the corpus,
            // and the staged semantic index for this one.
            phase(RepositoryIndexProgressPhase.SemanticBase);
            var semanticIndex = ResumeSemanticPhase(
                workSemanticIndex,
                semanticAdapterStatusPath,
                generation,
                dev);
            if (semanticIndex is null)
            {
                var built = await BuildSemanticIndexAsync(
                    snapshot.Root,
                    dev,
                    generation,
                    runtimeRoot,
                    cancellationToken);
                EnsureSemanticAdaptersSucceeded(built.AdapterStatuses);
                built.Index.Save(workSemanticIndex);
                WriteSemanticAdapterStatuses(semanticAdapterStatusPath, built.AdapterStatuses);
                semanticIndex = built;
            }

            ReportCsharpSemanticCoverage(
                snapshot.Root,
                semanticIndex.Index,
                semanticIndex.UncoveredProjects,
                requireSemantics);

            phase(RepositoryIndexProgressPhase.EmbeddingBase);
            var embedder = new BrokerEmbeddingClient(
                generation.EmbeddingModel,
                BrokerClientFactory.CreateDefault());
            var builder = new IndexBuilder(
                embedder,
                Console.Error.WriteLine,
                progress,
                // The definition bodies the phase above just produced. This is the whole reason
                // it runs first: a file the adapters covered is cut on its definitions instead
                // of on a line window, and a file they did not is cut exactly as before.
                SymbolDefinitionCatalog.FromSemanticIndex(semanticIndex.Index));
            await builder.BuildAsync(
                snapshot.Root,
                workIndex,
                force: false,
                cancellationToken,
                new IndexBuildContext(
                    dev.WorkingRoot.Value,
                    dev.HeadCommit,
                    dev.HeadTree,
                    dev.RepositoryId,
                    generation.Id),
                embeddingCheckpointPath,
                generation.EmbeddingDimension);
            var header = CodeIndex.Load(workIndex, withVectors: false);
            if (header.Dim != generation.EmbeddingDimension)
            {
                throw new InvalidDataException(
                    $"Embedding dimension {header.Dim} does not match expected " +
                    $"{generation.EmbeddingDimension}.");
            }

            // Copying a half-gigabyte corpus and hashing it twice is not instant either.
            phase(RepositoryIndexProgressPhase.PublishingGeneration);
            var published = store.PublishIndex(
                workIndex,
                generation,
                workSemanticIndex,
                semanticAdapterStatuses: semanticIndex.AdapterStatuses);
            DeleteEmbeddingCheckpoint(embeddingCheckpointPath);
            DeleteSemanticCheckpoint(workSemanticIndex, semanticAdapterStatusPath);
            return published;
        }
        finally
        {
            if (File.Exists(workIndex))
            {
                File.Delete(workIndex);
            }

            // The semantic checkpoint deliberately survives here. It is what an interrupted build
            // resumes from; the published generation deletes it, and retention collects one no
            // build came back for.
        }
    }

    /// <summary>
    /// The staged semantic index of an interrupted build, when it is the one this build needs.
    /// </summary>
    /// <remarks>
    /// On a repository the size of IntelWash the semantic phase is minutes, and it ran before
    /// embedding precisely so a failure is cheap. Without this, every resume paid those minutes
    /// again to produce a file identical to the one it had just deleted.
    ///
    /// Matching the generation id is the whole test, and it is sufficient rather than convenient:
    /// the id is derived from the repository, the git tree, the embedding model and dimension,
    /// and the chunk, normalization and semantic format versions. Two builds that agree on it
    /// are indexing the same tree with the same rules, so their semantic indexes are the same
    /// index. The repository and tree are compared anyway, because a file whose name says one
    /// generation and whose content says another is a corrupt file, not a stale one.
    ///
    /// Anything unreadable is treated as absent: the phase runs again and overwrites it. A
    /// checkpoint is an optimisation, and no optimisation is allowed to fail a build.
    /// </remarks>
    internal static SemanticBuildResult? ResumeSemanticPhase(
        string path,
        string statusPath,
        GenerationIdentity generation,
        WorkingIndexIdentity dev)
    {
        if (!File.Exists(path) || !File.Exists(statusPath))
        {
            return null;
        }

        try
        {
            var index = SemanticIndex.Load(path);
            if (index.GenerationId != generation.Id ||
                index.RepositoryId != dev.RepositoryId ||
                index.GitTree != dev.HeadTree)
            {
                return null;
            }

            var statuses = JsonSerializer.Deserialize<SemanticAdapterStatus[]>(
                File.ReadAllText(statusPath),
                LocalAiJson.Strict);
            if (statuses is null)
            {
                return null;
            }

            // A checkpoint is only written after the adapters were checked, so this holds unless
            // the file was edited. Checked anyway: publishing a generation whose adapters failed
            // is the one thing the semantic phase exists to prevent.
            EnsureSemanticAdaptersSucceeded(statuses);
            Console.Error.WriteLine(
                CliText.SemanticPhaseResumed(Path.GetFileName(path)));
            return new SemanticBuildResult(index, statuses);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException)
        {
            Console.Error.WriteLine(
                CliText.SemanticCheckpointUnusable(path, exception.Message));
            return null;
        }
    }

    internal static void WriteSemanticAdapterStatuses(
        string path,
        IReadOnlyList<SemanticAdapterStatus> statuses)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(statuses, LocalAiJson.Strict));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void DeleteSemanticCheckpoint(string path, string statusPath)
    {
        foreach (var file in new[] { path, statusPath })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    CliText.SemanticCheckpointNotRemoved(file, exception.Message));
            }
        }
    }

    /// <summary>
    /// Says which worktree is about to be cut by line window, and why, when an adapter failed
    /// during an overlay build. The base generation refuses to publish in that situation; a
    /// branch carries on, and the difference has to be visible to whoever reads the sync output.
    /// </summary>
    internal static void WarnDegradedSemanticOverlay(
        IReadOnlyList<SemanticAdapterStatus> statuses,
        string workingRoot)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var failures = statuses
            .Where(status => status.State == SemanticAdapterState.Failed)
            .ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        Console.Error.WriteLine(CliText.OverlayDegraded(
            workingRoot,
            string.Join("; ", failures.Select(failure => $"{failure.Name}: {failure.Message}"))));
    }

    /// <summary>
    /// Says so when semantic indexing did not cover the C# this repository has.
    ///
    /// Two shapes of the same silence. A workspace whose projects all failed to load still
    /// returns, the fallback writes an index with nothing in it, and sync exits 0 -- which is how
    /// a split between the Microsoft.CodeAnalysis package versions survived a green build and
    /// 1875 tests. And a repository with no solution file has one of its projects chosen and the
    /// rest left out, which reads exactly the same from outside: an index that is not empty, a
    /// status that says precise, and navigation that answers from text for most of the tree.
    ///
    /// Coverage is judged on what the loader reports it left out, not by counting .cs files
    /// against indexed ones. A repository legitimately holds C# no project compiles -- this one
    /// keeps test fixtures under tests/Fixtures -- and a check that warned about those would be
    /// ignored within a week.
    /// </summary>
    internal static void ReportCsharpSemanticCoverage(
        string sourceRoot,
        SemanticIndex index,
        IReadOnlyList<string>? uncoveredProjects,
        bool requireSemantics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(index);

        if (uncoveredProjects is { Count: > 0 })
        {
            Report(
                CliText.CoverageProjectsUncovered(
                    uncoveredProjects.Count,
                    string.Join(", ", uncoveredProjects)),
                requireSemantics);
            return;
        }

        if (index.Documents.Any(document => IsCsharp(document.RelPath)) ||
            !FileScanner.Enumerate(sourceRoot).Any(IsCsharp))
        {
            return;
        }

        Report(CliText.CoverageNoCsharp, requireSemantics);
    }

    private static void Report(string message, bool requireSemantics)
    {
        if (requireSemantics)
        {
            throw new InvalidOperationException(message);
        }

        Console.Error.WriteLine("WARNING: " + message);
    }

    private static bool IsCsharp(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    internal static void EnsureSemanticAdaptersSucceeded(
        IReadOnlyList<SemanticAdapterStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var failures = statuses
            .Where(status => status.State == SemanticAdapterState.Failed)
            .ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        var details = string.Join(
            "; ",
            failures.Select(failure => $"{failure.Name}: {failure.Message}"));
        throw new InvalidOperationException(CliText.AdaptersFailed(details));
    }

    private static void DeleteEmbeddingCheckpoint(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                CliText.EmbeddingCheckpointNotRemoved(path, exception.Message));
        }
    }

    private static async Task<SemanticBuildResult> BuildSemanticIndexAsync(
        string sourceRoot,
        WorkingIndexIdentity snapshot,
        GenerationIdentity generation,
        string? runtimeRoot,
        CancellationToken cancellationToken)
    {
        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            sourceRoot,
            message => Console.Error.WriteLine($"Roslyn: {message}"),
            cancellationToken);
        SemanticIndex languageIndex;
        if (loaded is null)
        {
            var empty = EmptySemanticIndex(generation) with
            {
                GitTree = snapshot.HeadTree,
                DirtyHash = snapshot.DirtyHash,
                BaseCommit = snapshot.HeadCommit,
            };
            languageIndex = new XamlSemanticIndexer().Supplement(empty, sourceRoot);
        }
        else
        {
            var csharp = await loaded.BuildIndexAsync(
                sourceRoot,
                new SemanticIndexBuildIdentity(
                    generation.RepositoryId,
                    generation.Id,
                    snapshot.HeadTree,
                    snapshot.DirtyHash,
                    snapshot.HeadCommit,
                    CommitTimestamp(snapshot.WorkingRoot.Value, snapshot.HeadCommit)),
                cancellationToken);
            languageIndex = new XamlSemanticIndexer().Supplement(csharp, sourceRoot);
        }

        var built = await RunScipAdaptersAsync(
            languageIndex,
            sourceRoot,
            runtimeRoot,
            cancellationToken);
        return built with { UncoveredProjects = loaded?.UncoveredProjects };
    }

    private static bool IsCurrentSemanticOverlay(
        string path,
        WorkingIndexIdentity snapshot,
        GenerationIdentity generation)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var overlay = SemanticIndexOverlay.Load(path);
            return string.Equals(overlay.RepositoryId, snapshot.RepositoryId, StringComparison.Ordinal) &&
                   string.Equals(overlay.GenerationId, generation.Id, StringComparison.Ordinal) &&
                   string.Equals(overlay.BaseGitTree, generation.DevTree, StringComparison.Ordinal) &&
                   string.Equals(overlay.GitTree, snapshot.HeadTree, StringComparison.Ordinal) &&
                   string.Equals(overlay.DirtyHash, snapshot.DirtyHash, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static async Task<SemanticBuildResult> RunScipAdaptersAsync(
        SemanticIndex index,
        string sourceRoot,
        string? runtimeRoot,
        CancellationToken cancellationToken)
    {
        // The adapter policy belongs to the installation, so it is read from the same one the
        // generation is published into. Reading it from the machine's own instead would let the
        // operator's configuration decide what an isolated sync indexes.
        var policy = new SemanticIndexingPolicyStore(
            string.IsNullOrWhiteSpace(runtimeRoot)
                ? SemanticIndexingPolicyStore.DefaultRuntimeRoot
                : runtimeRoot).Read();
        var statuses = new List<SemanticAdapterStatus>();
        var files = FileScanner.Enumerate(sourceRoot)
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
        if (!policy.Enabled)
        {
            statuses.Add(Skipped("typescript", "Semantic external adapters are disabled."));
            statuses.Add(Skipped("python", "Semantic external adapters are disabled."));
            return new SemanticBuildResult(index, statuses);
        }

        var importer = new ScipImporter(policy.ImportLimits());
        var runner = new ScipAdapterRunner(importer);
        var hasTypeScript = files.Any(path =>
            path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase));
        var typeScript = policy.TypeScript;
        if (!hasTypeScript)
        {
            statuses.Add(Skipped("typescript", "No TypeScript or JavaScript files detected."));
        }
        else if (!typeScript.Enabled)
        {
            statuses.Add(Skipped("typescript", "Adapter is disabled by policy."));
        }
        else
        {
            var arguments = typeScript.Arguments.ToList();
            string? syntheticWorkspace = null;
            var adapterRoot = sourceRoot;
            try
            {
                if (arguments.SequenceEqual(["index"], StringComparer.Ordinal))
                {
                    var projects = files
                        .Where(path => string.Equals(
                            Path.GetFileName(path),
                            "tsconfig.json",
                            StringComparison.OrdinalIgnoreCase))
                        .Select(path => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(
                            sourceRoot,
                            path.Replace('/', Path.DirectorySeparatorChar))))!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (projects.Length == 0)
                    {
                        syntheticWorkspace = CreateSyntheticTypeScriptWorkspace(
                            sourceRoot,
                            files.Where(IsTypeScriptSourceFile).ToArray());
                        adapterRoot = syntheticWorkspace;
                        projects = [syntheticWorkspace];
                    }

                    // scip-typescript 0.4.0 compares its project root with
                    // slash-normalized source paths using ordinal equality. Windows
                    // backslashes make it walk to the drive root and encode the
                    // temporary absolute path into every local symbol.
                    arguments.AddRange(projects.Select(project =>
                        project.Replace('\\', '/')));
                }

                var result = await runner.RunAsync(
                    index,
                    adapterRoot,
                    Spec("typescript", typeScript, arguments, policy),
                    cancellationToken);
                index = result.Index;
                statuses.Add(result.Status);
                ReportAdapter(result.Status);
            }
            finally
            {
                if (syntheticWorkspace is not null &&
                    Directory.Exists(syntheticWorkspace))
                {
                    Directory.Delete(syntheticWorkspace, recursive: true);
                }
            }
        }

        var hasPython = files.Any(path =>
            path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase));
        var python = policy.Python;
        if (!hasPython)
        {
            statuses.Add(Skipped("python", "No Python files detected."));
        }
        else if (!python.Enabled)
        {
            statuses.Add(Skipped("python", "Adapter is disabled by policy."));
        }
        else
        {
            var result = await runner.RunAsync(
                index,
                sourceRoot,
                Spec("python", python, python.Arguments, policy),
                cancellationToken);
            index = result.Index;
            statuses.Add(result.Status);
            ReportAdapter(result.Status);
        }

        return new SemanticBuildResult(index, statuses);
    }

    private static ScipAdapterSpec Spec(
        string name,
        ScipLanguageAdapterPolicy adapter,
        IReadOnlyList<string> arguments,
        SemanticIndexingPolicy policy) =>
        new(
            name,
            adapter.Executable,
            arguments,
            adapter.OutputFile,
            TimeSpan.FromSeconds(policy.TimeoutSeconds),
            policy.MaximumProcessOutputBytes,
            adapter.UnspecifiedPositionEncoding);

    internal static string CreateSyntheticTypeScriptWorkspace(
        string sourceRoot,
        IReadOnlyList<string> sourceFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(sourceFiles);
        if (sourceFiles.Count == 0)
        {
            throw new InvalidOperationException(
                "A synthetic TypeScript project requires at least one source file.");
        }

        var sourceRootFull = Path.GetFullPath(sourceRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"localai-scip-typescript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var relativeFiles = new List<string>(sourceFiles.Count);
            foreach (var path in sourceFiles)
            {
                var relative = path.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) || relative
                    .Split(Path.DirectorySeparatorChar)
                    .Any(segment => segment is "" or "." or ".."))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript source path is not canonical: '{path}'.");
                }

                var source = Path.GetFullPath(Path.Combine(sourceRootFull, relative));
                var sourcePrefix = sourceRootFull + Path.DirectorySeparatorChar;
                if (!source.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(source))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript source is unavailable: '{path}'.");
                }

                var target = Path.GetFullPath(Path.Combine(workspace, relative));
                var workspacePrefix = workspace + Path.DirectorySeparatorChar;
                if (!target.StartsWith(
                        workspacePrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript target escapes its workspace: '{path}'.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                relativeFiles.Add(path.Replace('\\', '/'));
            }

            var config = new
            {
                compilerOptions = new
                {
                    allowJs = true,
                    checkJs = false,
                    noEmit = true,
                    skipLibCheck = true,
                    target = "ES2020",
                    rootDir = ".",
                },
                files = relativeFiles,
            };
            File.WriteAllText(
                Path.Combine(workspace, "tsconfig.json"),
                JsonSerializer.Serialize(config),
                new UTF8Encoding(false));
            return workspace;
        }
        catch
        {
            Directory.Delete(workspace, recursive: true);
            throw;
        }
    }

    private static bool IsTypeScriptSourceFile(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase);

    private static SemanticAdapterStatus Skipped(string name, string message) =>
        new(name, SemanticAdapterState.Skipped, message, 0);

    private static void ReportAdapter(SemanticAdapterStatus status) =>
        Console.Error.WriteLine(
            $"SCIP {status.Name}: {status.State.ToString().ToLowerInvariant()} — {status.Message}");

    private static SemanticIndex EmptySemanticIndex(GenerationIdentity generation) =>
        new()
        {
            RepositoryId = generation.RepositoryId,
            GenerationId = generation.Id,
            GitTree = generation.DevTree,
            DirtyHash = null,
            BaseCommit = generation.DevCommit,
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    private static DateTime CommitTimestamp(string root, string commit)
    {
        var value = GitValue(root, "show", "-s", "--format=%cI", commit);
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.UtcDateTime
            : DateTime.UnixEpoch;
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
        var output = RepoLocator.GitOutputOrThrow(
            root,
            "worktree list --porcelain",
            "This repository's worktree list");
        return WorktreeInventory.ParsePorcelain(output);
    }

    private static string? GitValue(string root, params string[] arguments)
    {
        var output = RepoLocator.GitOutputBytes(root, arguments);
        return output is null
            ? null
            : Encoding.UTF8.GetString(output).Trim();
    }

    private sealed record Mainline(
        string Ref,
        WorkingIndexIdentity Identity);
}
