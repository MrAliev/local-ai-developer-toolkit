using System.Collections.Concurrent;
using System.Runtime;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace CodeSearch.Core.Search;

public sealed record OverlayStatus(
    string Path,
    bool Exists,
    int FileCount,
    int ChunkCount,
    int DeletedCount,
    long SizeBytes,
    string BaseCommit,
    string WorkingCommit,
    DateTime IndexedAtUtc)
{
    /// <summary>True when the base moved on after this overlay was computed against it.</summary>
    public bool BaseDrifted(string currentBaseCommit) =>
        Exists && BaseCommit.Length > 0 && currentBaseCommit.Length > 0 && BaseCommit != currentBaseCommit;
}

public sealed record IndexStatus(
    string WorkingRoot,
    string RepositoryRoot,
    string IndexPath,
    bool Exists,
    string Model,
    int Dim,
    int FileCount,
    int ChunkCount,
    long SizeBytes,
    string IndexedCommit,
    string CurrentCommit,
    DateTime IndexedAtUtc,
    string BaseRoot,
    bool RequiresOverlay,
    OverlayStatus Overlay,
    // Defaulted so the record keeps its shape for callers that only care about retrieval. A
    // generation published before semantic indexing existed carries no semantic.sidx, and that
    // is invisible in every other field here: the vectors are current, the commit has not
    // drifted, and navigation quietly answers from text matches instead.
    bool SemanticIndexPresent = false)
{
    public bool CommitDrifted =>
        Exists && IndexedCommit.Length > 0 && CurrentCommit.Length > 0 && IndexedCommit != CurrentCommit;

    /// <summary>True when the caller is working in the very checkout the base was built from.</summary>
    public bool WorkingRootIsBase =>
        !RequiresOverlay &&
        BaseRoot.Length > 0 &&
        string.Equals(
            BaseRoot.TrimEnd(Path.DirectorySeparatorChar),
            WorkingRoot.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

public sealed record SearchChunk(
    string ChunkId,
    string RelPath,
    int StartLine,
    int EndLine,
    ChunkKind Kind,
    string Symbol,
    string Signature,
    string Namespace,
    string Body);

/// <summary>
/// Resolves the repository, loads its base index plus this worktree's overlay, embeds the query
/// with the index's own model, and searches.
/// </summary>
public sealed class SearchService
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IEmbeddingClient> _embeddingClientFactory;
    private readonly Func<string, CancellationToken, Task<string>> _sourceTextReader;

    /// <summary>
    /// The installation this service reads indexes from. Null means the machine's own — the
    /// only value production ever uses. A caller supplies one to work against an installation
    /// that is not the current user's, which is what keeps tests out of the real runtime.
    /// </summary>
    private readonly string? _runtimeRoot;

    public SearchService(
        Func<string, IEmbeddingClient>? embeddingClientFactory = null,
        Func<string, CancellationToken, Task<string>>? sourceTextReader = null,
        string? runtimeRoot = null)
    {
        _embeddingClientFactory = embeddingClientFactory ??
            (model => new BrokerEmbeddingClient(
                model,
                BrokerClientFactory.CreateDefault()));
        _sourceTextReader = sourceTextReader ?? File.ReadAllTextAsync;
        _runtimeRoot = runtimeRoot;
    }

    public IEmbeddingClient CreateEmbeddingClient(string model) =>
        _embeddingClientFactory(model);

    /// <summary>
    /// How long a loaded index stays in memory after its last search.
    ///
    /// This matters because an MCP server lives as long as its Claude Code session, idle or not.
    /// Without eviction, one search in a session that is then left open pins ~700MB for hours.
    /// Reloading costs about a second, which is a fine price for a search nobody has asked for.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("CODESEARCH_IDLE_MINUTES"), out var minutes) && minutes > 0
            ? minutes
            : 10);

    /// <summary>
    /// When false, the query is embedded verbatim instead of with the model's instruction prefix.
    /// Only useful for A/B checking the prefix against a real index.
    /// </summary>
    public bool UseQueryInstruction { get; init; } = true;

    private sealed record CacheEntry(DateTime FileStamp, CodeIndex Index)
    {
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, string? root, SearchOptions options, CancellationToken ct = default)
    {
        var workingRoot = RepoLocator.ResolveWorkingRoot(root);
        var indexPath = RepoLocator.IndexPathFor(RepoLocator.ResolveRoot(root));
        var baseIndex = Load(indexPath);
        RequireSnapshotIdentity(baseIndex);

        var searchable = Compose(baseIndex, workingRoot);
        var resolvedOptions = SearchQualityProfile.Resolve(baseIndex.Model, options);

        // The model is read out of the index header rather than configured at the call site.
        // Embedding a query with a different model than the index was built with silently returns
        // garbage rankings instead of an error, so there is no option to get this wrong.
        var embedder = CreateEmbeddingClient(baseIndex.Model);
        var prompt = UseQueryInstruction ? QueryPrompt.ForQuery(baseIndex.Model, query) : query;
        float[][] vectors;
        try
        {
            vectors = await embedder.EmbedAsync(
                [prompt],
                LocalJobPriority.Interactive,
                QueryDeduplicationKey(baseIndex, prompt),
                ct);
        }
        catch (EmbeddingUnavailableException)
            when (resolvedOptions.AllowLexicalFallbackWhenEmbeddingsUnavailable)
        {
            return SearchEngine.SearchLexically(
                searchable,
                query,
                resolvedOptions,
                workingRoot);
        }

        var vector = vectors[0];

        // The raw query - not the instruction-wrapped prompt - drives lexical scoring, otherwise
        // words from the instruction itself would match chunk names.
        return SearchEngine.Search(
            searchable,
            vector,
            query,
            resolvedOptions,
            workingRoot);
    }

    public async Task<SearchChunk> GetChunkAsync(
        string chunkId,
        string? root,
        CancellationToken ct = default)
    {
        var requested = SearchChunkId.Parse(chunkId);
        var workingRoot = RepoLocator.ResolveWorkingRoot(root);
        var identity = RuntimeIndexLayout.Inspect(workingRoot, _runtimeRoot);

        if (!string.Equals(
                requested.RepositoryId,
                identity.RepositoryId,
                StringComparison.Ordinal))
        {
            SearchChunkResolver.ValidateSnapshot(
                requested,
                requested with { RepositoryId = identity.RepositoryId });
        }

        var indexPath = RepoLocator.IndexPathFor(RepoLocator.ResolveRoot(root));
        var baseIndex = Load(indexPath);
        var actualSnapshot = new SearchChunkId(
            identity.RepositoryId,
            baseIndex.GenerationId,
            identity.HeadTree,
            identity.DirtyHash,
            requested.Ordinal);
        SearchChunkResolver.ValidateSnapshot(requested, actualSnapshot);

        var searchable = Compose(baseIndex, workingRoot);
        var composedSnapshot = new SearchChunkId(
            searchable.RepositoryId,
            searchable.GenerationId,
            searchable.GitTree,
            searchable.DirtyHash,
            requested.Ordinal);
        SearchChunkResolver.ValidateSnapshot(requested, composedSnapshot);
        SearchChunkResolver.ValidateOrdinal(requested, searchable.ChunkCount);

        var meta = searchable.ChunkAt(requested.Ordinal);
        var relPath = searchable.PathOf(requested.Ordinal);
        if (!SafeSourcePath.TryResolveFile(
                workingRoot,
                relPath,
                out var fullPath,
                out var pathFailure))
        {
            throw SourcePathError(pathFailure);
        }

        ct.ThrowIfCancellationRequested();
        var sourceText = await _sourceTextReader(fullPath, ct);
        if (!CanonicalIndexText.Hash(sourceText).AsSpan().SequenceEqual(
                searchable.FileHashAt(requested.Ordinal)))
        {
            throw new SearchChunkResolutionException(
                "stale_source_content",
                "stale_source_content: The source file no longer matches the indexed content.");
        }

        var currentIdentity = RuntimeIndexLayout.Inspect(workingRoot, _runtimeRoot);
        SearchChunkResolver.ValidateSnapshot(
            requested,
            new SearchChunkId(
                currentIdentity.RepositoryId,
                baseIndex.GenerationId,
                currentIdentity.HeadTree,
                currentIdentity.DirtyHash,
                requested.Ordinal));

        var lines = SourceLines.Split(sourceText);
        if (meta.StartLine < 1 ||
            meta.EndLine < meta.StartLine ||
            meta.EndLine > lines.Length)
        {
            throw new SearchChunkResolutionException(
                "stale_source_range",
                "stale_source_range: The indexed line range no longer exists in the source file.");
        }

        var body = string.Join(
            "\n",
            lines[(meta.StartLine - 1)..meta.EndLine]);
        return new SearchChunk(
            chunkId,
            relPath,
            meta.StartLine,
            meta.EndLine,
            meta.Kind,
            meta.Symbol,
            meta.Signature,
            meta.Namespace,
            body);
    }

    private static void RequireSnapshotIdentity(CodeIndex index)
    {
        if (string.IsNullOrWhiteSpace(index.RepositoryId) ||
            string.IsNullOrWhiteSpace(index.GenerationId) ||
            string.IsNullOrWhiteSpace(index.GitTree))
        {
            throw new SearchNotReadyException(
                "The CodeSearch index predates snapshot-bound chunk retrieval. " +
                "Rebuild or migrate the index before searching.");
        }
    }

    private static SearchChunkResolutionException SourcePathError(
        SourcePathFailure failure) =>
        failure switch
        {
            SourcePathFailure.OutsideRoot => new(
                "unsafe_chunk_path",
                "unsafe_chunk_path: The indexed source path escapes the repository root."),
            SourcePathFailure.ReparsePoint => new(
                "unsafe_chunk_reparse_point",
                "unsafe_chunk_reparse_point: The indexed source path contains a " +
                "symbolic link or reparse point."),
            SourcePathFailure.Missing => new(
                "chunk_source_missing",
                "chunk_source_missing: The indexed source file no longer exists."),
            _ => new(
                "chunk_source_unavailable",
                "chunk_source_unavailable: The indexed source file is unavailable.")
        };

    private static string QueryDeduplicationKey(CodeIndex index, string prompt)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            index.Model + "\0" + index.GitCommit + "\0" + prompt);
        return "codesearch:query:" +
               Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    /// <summary>
    /// Lays this worktree's overlay over the base, when there is one to lay. A worktree with no
    /// overlay searches the base alone, which is correct only while it matches the base checkout -
    /// hence the staleness reporting in <see cref="Status"/>.
    /// </summary>
    private ISearchableIndex Compose(CodeIndex baseIndex, string workingRoot)
    {
        if (!string.IsNullOrWhiteSpace(baseIndex.GenerationId))
        {
            var identity = RuntimeIndexLayout.Inspect(workingRoot, _runtimeRoot);
            if (!string.Equals(
                    baseIndex.RepositoryId,
                    identity.RepositoryId,
                    StringComparison.Ordinal))
            {
                throw new SearchNotReadyException(
                    "The current index belongs to another repository.");
            }

            if (string.Equals(baseIndex.GitTree, identity.HeadTree, StringComparison.Ordinal) &&
                identity.DirtyHash is null)
            {
                return baseIndex;
            }

            var exactPath = RuntimeIndexLayout.OverlayPath(
                identity,
                baseIndex.GenerationId);
            if (!File.Exists(exactPath))
            {
                throw MissingOverlay(workingRoot);
            }

            var exactOverlay = Load(exactPath);
            if (!string.Equals(exactOverlay.GitTree, identity.HeadTree, StringComparison.Ordinal) ||
                !string.Equals(exactOverlay.DirtyHash, identity.DirtyHash, StringComparison.Ordinal))
            {
                throw MissingOverlay(workingRoot);
            }

            return new CompositeIndex(baseIndex, exactOverlay);
        }

        var overlayPath = RepoLocator.OverlayPathFor(workingRoot);
        if (SameRoot(baseIndex.Root, workingRoot))
        {
            return baseIndex;
        }

        if (!File.Exists(overlayPath))
        {
            throw MissingOverlay(workingRoot);
        }

        return new CompositeIndex(baseIndex, Load(overlayPath));
    }

    private static SearchNotReadyException MissingOverlay(string workingRoot) =>
        new(
            $"No exact current overlay exists for worktree '{workingRoot}'. " +
            "Stale or mixed results are blocked. Diagnose/restart MCP, use the LocalAi " +
            "CLI through the same broker, or continue with rg.");

    public IndexStatus Status(string? root)
    {
        var workingRoot = RepoLocator.ResolveWorkingRoot(root);
        var repositoryRoot = RepoLocator.ResolveRoot(root);
        var indexPath = RepoLocator.IndexPathFor(repositoryRoot);
        var currentCommit = RepoLocator.GitCommit(workingRoot);

        if (!File.Exists(indexPath))
        {
            var missingOverlay = new OverlayStatus(
                RepoLocator.OverlayPathFor(workingRoot),
                false,
                0,
                0,
                0,
                0,
                string.Empty,
                string.Empty,
                default);
            return new IndexStatus(
                workingRoot, repositoryRoot, indexPath, false, string.Empty, 0, 0, 0, 0,
                string.Empty, currentCommit, default, string.Empty, false, missingOverlay);
        }

        // Prefer the already-loaded index. A header-only load still walks every file and chunk
        // record - 43k of them on IntelWash - which is real latency to pay on a status line that
        // search_code prints after every query.
        var index = TryGetCached(indexPath) ?? CodeIndex.Load(indexPath, withVectors: false);
        var identity = string.IsNullOrWhiteSpace(index.GenerationId)
            ? null
            : RuntimeIndexLayout.Inspect(workingRoot, _runtimeRoot);
        var requiresOverlay = identity is null
            ? !SameRoot(index.Root, workingRoot)
            : !string.Equals(index.GitTree, identity.HeadTree, StringComparison.Ordinal) ||
              identity.DirtyHash is not null;
        var overlay = OverlayStatusFor(workingRoot, index, identity);
        var currentBaseCommit = CurrentBaseCommit(
            workingRoot,
            index,
            currentCommit,
            identity);

        return new IndexStatus(
            workingRoot,
            repositoryRoot,
            indexPath,
            true,
            index.Model,
            index.Dim,
            index.Files.Count,
            index.Chunks.Count,
            new FileInfo(indexPath).Length,
            index.GitCommit,
            currentBaseCommit,
            index.IndexedAtUtc,
            index.Root,
            requiresOverlay,
            overlay,
            SemanticIndexPresent(index, identity));
    }

    /// <summary>
    /// Whether the current generation carries a semantic index, by one stat rather than a manifest
    /// read — verifying a manifest rehashes a half-gigabyte corpus, which is not a price a status
    /// line printed after every query can pay.
    /// </summary>
    private static bool SemanticIndexPresent(
        CodeIndex index,
        WorkingIndexIdentity? identity)
    {
        if (identity is null || string.IsNullOrWhiteSpace(index.GenerationId))
        {
            return false;
        }

        try
        {
            return File.Exists(
                new GenerationStore(identity.RepositoryRuntimeRoot)
                    .SemanticIndexPath(index.GenerationId));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string CurrentBaseCommit(
        string workingRoot,
        CodeIndex index,
        string workingCommit,
        WorkingIndexIdentity? identity)
    {
        if (string.IsNullOrWhiteSpace(index.GenerationId))
        {
            return SameRoot(index.Root, workingRoot)
                ? workingCommit
                : RepoLocator.GitCommit(index.Root);
        }

        if (identity is null)
        {
            return index.GitCommit;
        }

        var manifest = new RepositoryManifestStore(
            identity.RepositoryRuntimeRoot).Read();
        if (manifest is null ||
            !string.Equals(
                manifest.RepositoryId,
                index.RepositoryId,
                StringComparison.Ordinal))
        {
            return index.GitCommit;
        }

        return RepoLocator.GitOutput(
            workingRoot,
            $"rev-parse --verify {manifest.DevRef}^{{commit}}")
            ?? index.GitCommit;
    }

    private OverlayStatus OverlayStatusFor(
        string workingRoot,
        CodeIndex baseIndex,
        WorkingIndexIdentity? identity)
    {
        var path = string.IsNullOrWhiteSpace(baseIndex.GenerationId)
            ? RepoLocator.OverlayPathFor(workingRoot)
            : RuntimeIndexLayout.OverlayPath(
                identity ?? RuntimeIndexLayout.Inspect(workingRoot, _runtimeRoot),
                baseIndex.GenerationId);
        if (!File.Exists(path))
        {
            return new OverlayStatus(path, false, 0, 0, 0, 0, string.Empty, string.Empty, default);
        }

        var overlay = TryGetCached(path) ?? CodeIndex.Load(path, withVectors: false);
        return new OverlayStatus(
            path,
            true,
            overlay.Files.Count,
            overlay.Chunks.Count,
            overlay.DeletedPaths.Count,
            new FileInfo(path).Length,
            overlay.BaseCommit,
            overlay.GitCommit,
            overlay.IndexedAtUtc);
    }

    public CodeIndex Load(string indexPath)
    {
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException(
                $"No index at '{indexPath}'. Build it with: codesearch index --root <repo>", indexPath);
        }

        EvictIdle();

        var cached = TryGetCached(indexPath);
        if (cached is not null)
        {
            return cached;
        }

        var index = CodeIndex.Load(indexPath);
        _cache[indexPath] = new CacheEntry(File.GetLastWriteTimeUtc(indexPath), index);
        return index;
    }

    /// <summary>
    /// Returns the in-memory index only if the file behind it hasn't been rewritten. A long-lived
    /// MCP server would otherwise keep serving a stale index after a reindex, so the write
    /// timestamp is part of the cache key.
    /// </summary>
    private CodeIndex? TryGetCached(string indexPath)
    {
        if (!File.Exists(indexPath) || !_cache.TryGetValue(indexPath, out var cached))
        {
            return null;
        }

        if (cached.FileStamp != File.GetLastWriteTimeUtc(indexPath))
        {
            return null;
        }

        cached.LastUsedUtc = DateTime.UtcNow;
        return cached.Index;
    }

    /// <summary>
    /// Drops indexes untouched for longer than <see cref="IdleTimeout"/>. Called on every load
    /// rather than from a timer: a server with no traffic has nothing to evict anyway, and a
    /// timer would keep an otherwise idle process waking up.
    /// </summary>
    private void EvictIdle()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (path, entry) in _cache)
        {
            if (entry.LastUsedUtc < cutoff)
            {
                _cache.TryRemove(path, out _);
            }
        }
    }

    public void Invalidate(string indexPath) => _cache.TryRemove(indexPath, out _);

    /// <summary>
    /// Drops every cached index and hands the memory back to the OS, reporting what actually
    /// happened rather than what was requested.
    ///
    /// Three steps, and all three are needed. Collecting alone leaves the vectors on the large
    /// object heap, which is never compacted by default. Compacting alone frees the managed heap
    /// but leaves the pages in the process's working set, so the OS still reports ~700MB resident
    /// - measured, not assumed: an earlier version reported "freed 675MB" while RSS fell by 13MB.
    /// Trimming the working set is what makes the release real and visible.
    /// </summary>
    public UnloadReport UnloadAll()
    {
        // Working set, not GC.GetTotalMemory: the managed-heap figure stays around 700MB right
        // after the collection even though the pages are gone (measured - RSS fell 767MB -> 21MB
        // while GetTotalMemory still read 699MB).
        var before = Environment.WorkingSet;
        var count = _cache.Count;

        foreach (var (path, _) in _cache)
        {
            _cache.TryRemove(path, out _);
        }

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var trimmed = NativeMemory.TrimWorkingSet();

        // Read after a settle pause. Immediately post-trim the working set reads as near zero,
        // because even the pages of the code doing the reading have just been evicted; they fault
        // straight back in and it settles around 20MB. Reporting the instantaneous ~0 would be a
        // prettier number and a false one.
        Thread.Sleep(250);
        var after = Environment.WorkingSet;

        return new UnloadReport(
            count,
            (int)Math.Max(0, (before - after) / (1024 * 1024)),
            (int)(after / (1024 * 1024)),
            trimmed);
    }

    /// <summary>Indexes currently held in memory, with their approximate footprint in MB.</summary>
    public IReadOnlyList<(string Path, int Megabytes, DateTime LastUsedUtc)> Loaded() =>
        _cache.Select(kv => (
                kv.Key,
                (int)((long)kv.Value.Index.Chunks.Count * kv.Value.Index.Dim * sizeof(float) / (1024 * 1024)),
                kv.Value.LastUsedUtc))
            .ToList();

    private static bool SameRoot(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

