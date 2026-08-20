using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalAi.Repository;
using System.Text;
using System.Text.Json;

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

    internal sealed record SemanticBuildResult(
        SemanticIndex Index,
        IReadOnlyList<SemanticAdapterStatus> AdapterStatuses);

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
        string? runtimeRoot = null)
    {
        var requested = RuntimeIndexLayout.Inspect(workingRoot, runtimeRoot);
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
                CurrentChunkFormatVersion,
                CodeIndex.CurrentVersion,
                CurrentNormalizationVersion,
                1,
                CurrentSemanticGenerationVersion);
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
                        mainline.Identity.WorkingRoot,
                        progress),
                    phase => ReportPhase(phase, mainline.Identity.WorkingRoot),
                    runtimeRoot,
                    cancellationToken);
            }

            var targets = includeOverlays
                ? generationChanged
                    ? worktrees
                    : worktrees.Where(
                        item => SamePath(item.Path, requested.WorkingRoot)).ToArray()
                : [];
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
                    identity = RuntimeIndexLayout.Inspect(worktree.Path, runtimeRoot);
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
                        identity.WorkingRoot);
                    var semanticBuild = await BuildSemanticIndexAsync(
                        identity.WorkingRoot,
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
                        identity.WorkingRoot);
                    var baseSemanticIndex = SemanticIndex.Load(
                        store.SemanticIndexPath(generation.Identity.Id));
                    var semanticOverlay = SemanticIndexOverlay.Create(
                        baseSemanticIndex,
                        semanticBuild.Index,
                        RuntimeIndexLayout.GetDirtyPaths(identity.WorkingRoot));
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
                            identity.WorkingRoot,
                            progress),
                        definitions);
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
                            identity.DirtyHash),
                        embeddingCheckpointPath,
                        generation.Identity.EmbeddingDimension);
                    DeleteEmbeddingCheckpoint(embeddingCheckpointPath);
                    builtOverlay = true;
                }

                if (builtOverlay)
                {
                    overlaysBuilt++;
                }
            }

            ReportPhase(RepositoryIndexProgressPhase.Publishing, requested.WorkingRoot);
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
            PruneSupersededGenerations(requested.RepositoryRuntimeRoot, runtimeRoot);

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
    internal static void PruneSupersededGenerations(
        string repositoryRuntimeRoot,
        string? runtimeRoot)
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
                DateTimeOffset.UtcNow);
            if (result.ActionCount > 0)
            {
                Console.Error.WriteLine(
                    $"Retention: removed {result.GenerationsRemoved.Count} superseded " +
                    $"generation(s), {result.OverlaysRemoved.Count} overlay set(s) and " +
                    $"{result.StagingRemoved.Count} staging file(s), " +
                    $"{result.BytesReclaimed / (1024 * 1024)} MB.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(
                $"Retention sweep skipped: {exception.Message}. The published index is unaffected.");
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
        Action<RepositoryIndexProgressPhase> phase,
        string? runtimeRoot,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(
            dev.RepositoryRuntimeRoot,
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

            using var snapshot = CommitSnapshot.Create(dev.WorkingRoot, dev.HeadCommit);

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
                    dev.WorkingRoot,
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
                $"Semantic phase resumed from '{Path.GetFileName(path)}'.");
            return new SemanticBuildResult(index, statuses);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException)
        {
            Console.Error.WriteLine(
                $"Semantic checkpoint '{path}' was not reusable and will be rebuilt: " +
                exception.Message);
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
                    $"Semantic checkpoint '{file}' could not be removed: {exception.Message}");
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

        Console.Error.WriteLine(
            $"Semantic overlay for '{workingRoot}' is degraded: " +
            string.Join("; ", failures.Select(failure => $"{failure.Name}: {failure.Message}")) +
            ". Files this worktree changed are cut by line window until the adapter works " +
            "again; the base generation is unaffected.");
    }

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
        throw new InvalidOperationException(
            "Semantic generation was not published because required adapters failed: " +
            details);
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
                $"Embedding checkpoint '{path}' could not be removed: {exception.Message}");
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
                    CommitTimestamp(snapshot.WorkingRoot, snapshot.HeadCommit)),
                cancellationToken);
            languageIndex = new XamlSemanticIndexer().Supplement(csharp, sourceRoot);
        }

        return await RunScipAdaptersAsync(
            languageIndex,
            sourceRoot,
            runtimeRoot,
            cancellationToken);
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
