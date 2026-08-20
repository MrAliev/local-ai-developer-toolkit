using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

public sealed record BuildResult(
    int FileCount,
    int ChunkCount,
    int FilesReused,
    int FilesEmbedded,
    int ChunksEmbedded,
    TimeSpan Elapsed,
    string IndexPath);

public sealed record BuildPlan(int FileCount, int FilesChanged, int ChunksToEmbed);

public sealed record IndexBuildProgress(
    int ProcessedChunks,
    int TotalChunks,
    double ChunksPerSecond,
    TimeSpan? EstimatedRemaining);

public sealed record IndexBuildContext(
    string Root,
    string GitCommit,
    string GitTree,
    string RepositoryId,
    string GenerationId,
    string? DirtyHash = null);

/// <summary>
/// Builds and incrementally refreshes an index. Incrementality is by file content hash: an
/// unchanged file keeps its existing chunks and vectors verbatim, so a refresh right after a
/// commit only re-embeds what that commit touched.
/// </summary>
public sealed class IndexBuilder(
    IEmbeddingClient embedder,
    Action<string>? log = null,
    Action<IndexBuildProgress>? progress = null,
    SymbolDefinitionCatalog? definitions = null)
{
    /// <summary>
    /// Definition bodies for the tree being indexed, from the semantic phase that ran before
    /// this one. Empty for a build with no semantic data — an overlay, a repository whose
    /// adapters are disabled, a machine without the external indexers — and every file then
    /// chunks the way it did before symbol-aware chunking existed.
    /// </summary>
    private readonly SymbolDefinitionCatalog _definitions = definitions ?? SymbolDefinitionCatalog.Empty;

    /// <summary>
    /// Batches are capped by total characters, not item count. Chunk sizes vary by an order of
    /// magnitude, so a fixed count either starves the GPU on small chunks or sends an oversized
    /// request on large ones.
    /// </summary>
    private const int InitialBatchChars = 48_000;
    private const int MinimumBatchChars = 8_000;
    private const int MaximumBatchChars = 400_000;
    private const int BatchMaxItems = 512;

    /// <summary>
    /// The size is measured, not configured, because the right one differs by an order of
    /// magnitude between machines.
    ///
    /// Every request carries a fixed cost that has nothing to do with the model: assembling the
    /// batch, writing the job to the durable queue, polling for its result, writing the vectors
    /// back. Measured here, that was around 2.4 seconds against 3.6 seconds of actual embedding
    /// — the adapter idle for 40% of the run. Bigger batches amortise it away.
    ///
    /// A larger constant would have paid for that on a fast adapter and charged for it on a slow
    /// one: the same batch that takes 4 seconds on a desktop card takes minutes on an integrated
    /// one, and the broker's watchdog starts probing a job that has been silent for ten minutes.
    /// So the budget follows the observed throughput toward a target duration instead, and a
    /// slow machine simply keeps sending small batches.
    /// </summary>
    private static readonly TimeSpan TargetBatchDuration = TimeSpan.FromSeconds(15);

    private readonly Action<string> _log = log ?? (_ => { });
    private readonly Action<IndexBuildProgress> _progress = progress ?? (_ => { });

    public async Task<BuildResult> BuildAsync(
        string root,
        string indexPath,
        bool force = false,
        CancellationToken ct = default,
        IndexBuildContext? context = null,
        string? embeddingCheckpointPath = null,
        int? expectedEmbeddingDimension = null)
    {
        var stopwatch = Stopwatch.StartNew();
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

        var files = FileScanner.Enumerate(root);
        _log($"Scanned {root}: {files.Count} indexable files.");

        var existing = LoadReusable(indexPath, force);
        var previousByPath = BuildLookup(existing);

        var (reused, changed) = Partition(root, files, previousByPath, out var hashes);
        _log($"Unchanged: {reused.Count}. New or changed: {changed.Count}.");

        var freshChunks = ChunkFiles(root, changed, ct);
        var totalToEmbed = freshChunks.Values.Sum(c => c.Count);
        _log($"Chunked {changed.Count} files into {totalToEmbed} chunks to embed.");
        _progress(new IndexBuildProgress(0, totalToEmbed, 0, null));

        var checkpoint = embeddingCheckpointPath is null
            ? null
            : new EmbeddingCheckpointStore(
                embeddingCheckpointPath,
                embedder.Model,
                expectedEmbeddingDimension,
                _log);
        var vectorsByPath = await EmbedAsync(freshChunks, totalToEmbed, checkpoint, ct);

        var assembled = Assemble(files, previousByPath, existing, freshChunks, vectorsByPath, hashes, out var dim);

        if (dim == 0)
        {
            throw new InvalidOperationException(
                "Nothing was embedded and no previous index exists, so vector width is unknown.");
        }

        var index = new CodeIndex
        {
            Dim = dim,
            Model = embedder.Model,
            Root = context?.Root ?? root,
            GitCommit = context?.GitCommit ?? RepoLocator.GitCommit(root),
            GitTree = context?.GitTree ??
                      RepoLocator.GitOutput(root, "rev-parse HEAD^{tree}") ??
                      string.Empty,
            RepositoryId = context?.RepositoryId ?? string.Empty,
            GenerationId = context?.GenerationId ?? string.Empty,
            DirtyHash = context?.DirtyHash,
            IndexedAtUtc = DateTime.UtcNow,
            Files = assembled.Files,
            Chunks = assembled.Chunks,
            Vectors = assembled.Vectors,
        };

        index.Save(indexPath);
        stopwatch.Stop();

        _log($"Saved {indexPath} ({assembled.Chunks.Count} chunks, {assembled.Files.Count} files) " +
             $"in {stopwatch.Elapsed.TotalSeconds:F1}s.");

        return new BuildResult(
            assembled.Files.Count,
            assembled.Chunks.Count,
            reused.Count,
            changed.Count,
            totalToEmbed,
            stopwatch.Elapsed,
            indexPath);
    }

    /// <summary>
    /// Builds this working tree's overlay against a base index: only files whose content differs
    /// from the base get embedded, plus a list of files the base has and this tree does not.
    ///
    /// The base is loaded WITHOUT vectors - only its per-file hashes are needed to decide what
    /// differs, so building an overlay never pays the ~700MB read of the base vector block.
    /// </summary>
    public async Task<BuildResult> BuildOverlayAsync(
        string workingRoot,
        string baseIndexPath,
        string overlayPath,
        CancellationToken ct = default,
        IndexBuildContext? context = null,
        string? embeddingCheckpointPath = null,
        int? expectedEmbeddingDimension = null)
    {
        var stopwatch = Stopwatch.StartNew();
        workingRoot = Path.GetFullPath(workingRoot).TrimEnd(Path.DirectorySeparatorChar);

        var baseIndex = CodeIndex.Load(baseIndexPath, withVectors: false);
        if (!string.Equals(baseIndex.Model, embedder.Model, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Base index uses '{baseIndex.Model}' but this run uses '{embedder.Model}'. " +
                "An overlay must be embedded with the base's model or the vectors cannot be compared.");
        }

        var files = FileScanner.Enumerate(workingRoot);
        var baseByPath = baseIndex.Files.ToDictionary(f => f.RelPath, StringComparer.OrdinalIgnoreCase);
        _log($"Scanned {workingRoot}: {files.Count} files. Base holds {baseByPath.Count}.");

        var (changed, hashes) = SelectDivergent(workingRoot, files, baseByPath);
        var deleted = baseByPath.Keys
            .Where(path => !hashes.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log($"Differs from base: {changed.Count} files. Present in base but not here: {deleted.Count}.");

        var existingOverlay = LoadReusable(overlayPath, force: false);
        var overlayByPath = BuildLookup(existingOverlay);

        var toChunk = changed
            .Where(path => !(overlayByPath.TryGetValue(path, out var prior) &&
                             prior.Hash.AsSpan().SequenceEqual(hashes[path])))
            .ToList();

        _log($"Reused from previous overlay: {changed.Count - toChunk.Count}. To embed: {toChunk.Count}.");

        var freshChunks = ChunkFiles(workingRoot, toChunk, ct);
        var totalToEmbed = freshChunks.Values.Sum(c => c.Count);
        _progress(new IndexBuildProgress(0, totalToEmbed, 0, null));
        var checkpoint = embeddingCheckpointPath is null
            ? null
            : new EmbeddingCheckpointStore(
                embeddingCheckpointPath,
                embedder.Model,
                expectedEmbeddingDimension ?? baseIndex.Dim,
                _log);
        var vectorsByPath = await EmbedAsync(freshChunks, totalToEmbed, checkpoint, ct);

        var assembled = Assemble(
            changed, overlayByPath, existingOverlay, freshChunks, vectorsByPath, hashes, out var dim);

        if (dim == 0)
        {
            // Nothing diverges at all: still write the overlay so deletions are recorded and the
            // search path does not keep falling back to "no overlay exists".
            dim = baseIndex.Dim;
        }

        var overlay = new CodeIndex
        {
            Dim = dim,
            Model = embedder.Model,
            Root = context?.Root ?? workingRoot,
            GitCommit = context?.GitCommit ?? RepoLocator.GitCommit(workingRoot),
            GitTree = context?.GitTree ??
                      RepoLocator.GitOutput(workingRoot, "rev-parse HEAD^{tree}") ??
                      string.Empty,
            RepositoryId = context?.RepositoryId ?? baseIndex.RepositoryId,
            GenerationId = context?.GenerationId ?? baseIndex.GenerationId,
            DirtyHash = context?.DirtyHash,
            IndexedAtUtc = DateTime.UtcNow,
            Files = assembled.Files,
            Chunks = assembled.Chunks,
            Vectors = assembled.Vectors,
            BaseCommit = baseIndex.GitCommit,
            DeletedPaths = deleted,
        };

        overlay.Save(overlayPath);
        stopwatch.Stop();

        _log($"Saved overlay {overlayPath}: {assembled.Chunks.Count} chunks over {assembled.Files.Count} " +
             $"files, {deleted.Count} deletions, in {stopwatch.Elapsed.TotalSeconds:F1}s.");

        return new BuildResult(
            assembled.Files.Count,
            assembled.Chunks.Count,
            changed.Count - toChunk.Count,
            toChunk.Count,
            totalToEmbed,
            stopwatch.Elapsed,
            overlayPath);
    }

    /// <summary>
    /// Files whose content is not byte-identical to the base. Everything else is covered by the
    /// base's own vectors, so the overlay must not duplicate it.
    /// </summary>
    private static (List<string> Changed, Dictionary<string, byte[]> Hashes) SelectDivergent(
        string root, List<string> files, Dictionary<string, IndexedFile> baseByPath)
    {
        var computed = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(files, relPath =>
        {
            var hash = HashFile(Path.Combine(root, relPath));
            if (hash is not null)
            {
                computed[relPath] = hash;
            }
        });

        var changed = new List<string>();
        foreach (var relPath in files)
        {
            if (!computed.TryGetValue(relPath, out var hash))
            {
                continue;
            }

            if (!baseByPath.TryGetValue(relPath, out var inBase) ||
                !inBase.Hash.AsSpan().SequenceEqual(hash))
            {
                changed.Add(relPath);
            }
        }

        return (changed, new Dictionary<string, byte[]>(computed, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Costs out a refresh without embedding anything. The MCP server uses this to refuse a cold
    /// build inline (which would take many minutes and time the tool call out) and hand back a
    /// CLI command instead.
    /// </summary>
    public BuildPlan Plan(string root, string indexPath, bool force = false)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var files = FileScanner.Enumerate(root);
        var existing = LoadReusable(indexPath, force);
        var previousByPath = BuildLookup(existing);
        var (_, changed) = Partition(root, files, previousByPath, out _);

        // Chunk counting is a Roslyn parse per changed file - cheap next to embedding, and it
        // makes the estimate exact rather than a guess from file sizes.
        var chunks = ChunkFiles(root, changed, CancellationToken.None).Values.Sum(c => c.Count);
        return new BuildPlan(files.Count, changed.Count, chunks);
    }

    private CodeIndex? LoadReusable(string indexPath, bool force)
    {
        if (force || !File.Exists(indexPath))
        {
            return null;
        }

        try
        {
            var index = CodeIndex.Load(indexPath);
            if (index.FormatVersion < CodeIndex.CurrentVersion)
            {
                _log($"Existing index is format v{index.FormatVersion}; v{CodeIndex.CurrentVersion} " +
                     "adds language-independent lexical text - rebuilding once.");
                return null;
            }

            if (!string.Equals(index.Model, embedder.Model, StringComparison.OrdinalIgnoreCase))
            {
                // Vectors from different models are not comparable, so a model switch is a full
                // rebuild whether the files changed or not.
                _log($"Existing index was built with '{index.Model}', now using '{embedder.Model}' - rebuilding from scratch.");
                return null;
            }

            _log($"Existing index: {index.Chunks.Count} chunks over {index.Files.Count} files.");
            return index;
        }
        catch (Exception ex)
        {
            _log($"Could not reuse existing index ({ex.Message}) - rebuilding from scratch.");
            return null;
        }
    }

    private static Dictionary<string, IndexedFile> BuildLookup(CodeIndex? index) =>
        index is null
            ? new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase)
            : index.Files.ToDictionary(f => f.RelPath, StringComparer.OrdinalIgnoreCase);

    private static (List<string> Reused, List<string> Changed) Partition(
        string root,
        List<string> files,
        Dictionary<string, IndexedFile> previous,
        out Dictionary<string, byte[]> hashes)
    {
        var reused = new List<string>();
        var changed = new List<string>();
        var computed = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(files, relPath =>
        {
            var hash = HashFile(Path.Combine(root, relPath));
            if (hash is not null)
            {
                computed[relPath] = hash;
            }
        });

        foreach (var relPath in files)
        {
            if (!computed.TryGetValue(relPath, out var hash))
            {
                continue;
            }

            if (previous.TryGetValue(relPath, out var prior) && prior.Hash.AsSpan().SequenceEqual(hash))
            {
                reused.Add(relPath);
            }
            else
            {
                changed.Add(relPath);
            }
        }

        hashes = new Dictionary<string, byte[]>(computed, StringComparer.OrdinalIgnoreCase);
        return (reused, changed);
    }

    private static byte[]? HashFile(string path)
    {
        try
        {
            return CanonicalIndexText.Hash(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Dictionary<string, List<Chunk>> ChunkFiles(
        string root, List<string> relPaths, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, List<Chunk>>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(relPaths, new ParallelOptions { CancellationToken = ct }, relPath =>
        {
            var chunker = ChunkerFactory.Resolve(relPath, _definitions);
            if (chunker is null)
            {
                return;
            }

            try
            {
                var content = CanonicalIndexText.Read(Path.Combine(root, relPath));
                var chunks = chunker.Split(relPath, content).ToList();
                if (chunks.Count > 0)
                {
                    result[relPath] = chunks;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished or is locked mid-scan simply doesn't get indexed this run.
            }
        });

        return new Dictionary<string, List<Chunk>>(result, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, float[][]>> EmbedAsync(
        Dictionary<string, List<Chunk>> chunksByFile,
        int total,
        EmbeddingCheckpointStore? checkpoint,
        CancellationToken ct)
    {
        var vectors = chunksByFile.ToDictionary(
            kv => kv.Key,
            kv => new float[kv.Value.Count][],
            StringComparer.OrdinalIgnoreCase);

        if (total == 0)
        {
            return vectors;
        }

        var queue = new List<(string RelPath, int Slot, string Text)>(total);
        var restored = 0;
        foreach (var (relPath, chunks) in chunksByFile)
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                var text = CanonicalIndexText.Normalize(chunks[i].EmbedText);
                if (checkpoint?.TryGet(text, out var vector) == true)
                {
                    vectors[relPath][i] = vector;
                    restored++;
                }
                else
                {
                    queue.Add((relPath, i, text));
                }
            }
        }

        if (restored > 0)
        {
            _log($"Restored {restored}/{total} chunks from the embedding checkpoint.");
            _progress(new IndexBuildProgress(
                restored,
                total,
                0,
                restored == total ? TimeSpan.Zero : null));
        }

        var stopwatch = Stopwatch.StartNew();
        var done = restored;
        var embeddedThisRun = 0;
        var position = 0;
        var budget = InitialBatchChars;

        while (position < queue.Count)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new List<(string RelPath, int Slot, string Text)>();
            var chars = 0;
            while (position < queue.Count && batch.Count < BatchMaxItems &&
                   (batch.Count == 0 || chars + queue[position].Text.Length <= budget))
            {
                chars += queue[position].Text.Length;
                batch.Add(queue[position]);
                position++;
            }

            var batchStarted = stopwatch.Elapsed;
            var embeddings = await EmbedBatchAsync(batch, ct);
            checkpoint?.SaveBatch(
                batch.Select(item => item.Text).ToArray(),
                embeddings);
            budget = NextBudget(budget, chars, stopwatch.Elapsed - batchStarted);
            for (var i = 0; i < batch.Count; i++)
            {
                vectors[batch[i].RelPath][batch[i].Slot] = embeddings[i];
            }

            done += batch.Count;
            embeddedThisRun += batch.Count;
            var rate = embeddedThisRun / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            var remaining = TimeSpan.FromSeconds((queue.Count - done) / Math.Max(0.001, rate));
            _log($"Embedded {done}/{queue.Count} chunks ({rate:F1}/s, ~{remaining.TotalMinutes:F1} min left)");
            _progress(new IndexBuildProgress(done, queue.Count, rate, remaining));
        }

        return vectors;
    }

    /// <summary>
    /// The next character budget, from what the last batch actually cost.
    ///
    /// Moves by at most a factor of two per step. A single slow batch — a cold model, another
    /// client's job ahead in the queue — should nudge the size, not halve or double it, and the
    /// clamp keeps a machine that is briefly starved from collapsing to one chunk per request.
    /// </summary>
    internal static int NextBudget(int budget, int chars, TimeSpan elapsed)
    {
        if (chars <= 0 || elapsed <= TimeSpan.Zero)
        {
            return budget;
        }

        var perSecond = chars / elapsed.TotalSeconds;
        var target = perSecond * TargetBatchDuration.TotalSeconds;
        var bounded = Math.Clamp(target, budget / 2d, budget * 2d);
        return (int)Math.Clamp(bounded, MinimumBatchChars, MaximumBatchChars);
    }

    /// <summary>
    /// One failing chunk used to end a multi-hour run: the broker exception propagated straight
    /// out of the build and every embedding computed so far was discarded, because a generation
    /// is published atomically and nothing is checkpointed. Halving the batch on failure isolates
    /// the offender - a transient fault clears on the smaller retry, and a deterministic one is
    /// reported against the exact file and chunk instead of an opaque broker job id.
    /// </summary>
    private async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<(string RelPath, int Slot, string Text)> batch,
        CancellationToken ct)
    {
        var inputs = batch.Select(b => b.Text).ToList();
        try
        {
            return await embedder.EmbedAsync(
                inputs,
                LocalJobPriority.Background,
                EmbeddingDeduplicationKey(inputs),
                ct);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
                not EmbeddingUnavailableException &&
            batch.Count > 1)
        {
            _log($"Embedding {batch.Count} chunks failed ({exception.Message}); " +
                 "halving the batch to isolate the chunk.");
            var half = batch.Count / 2;
            var head = await EmbedBatchAsync([.. batch.Take(half)], ct);
            var tail = await EmbedBatchAsync([.. batch.Skip(half)], ct);
            return [.. head, .. tail];
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
                not EmbeddingUnavailableException and
                not EmbeddingChunkException)
        {
            var chunk = batch[0];
            throw new EmbeddingChunkException(
                $"Chunk {chunk.Slot} of '{chunk.RelPath}' ({chunk.Text.Length} characters) " +
                $"could not be embedded: {exception.Message}",
                exception);
        }
    }

    private string EmbeddingDeduplicationKey(IReadOnlyList<string> inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(embedder.Model));
        foreach (var input in inputs)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(input));
            hash.AppendData([0]);
        }

        return "codesearch:index:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (List<IndexedFile> Files, List<ChunkMeta> Chunks, float[] Vectors) Assemble(
        List<string> files,
        Dictionary<string, IndexedFile> previous,
        CodeIndex? existing,
        Dictionary<string, List<Chunk>> freshChunks,
        Dictionary<string, float[][]> freshVectors,
        Dictionary<string, byte[]> hashes,
        out int dim)
    {
        dim = existing?.Dim ?? 0;
        if (dim == 0)
        {
            foreach (var vectors in freshVectors.Values)
            {
                if (vectors.Length > 0 && vectors[0] is { Length: > 0 })
                {
                    dim = vectors[0].Length;
                    break;
                }
            }
        }

        // Two passes: total up the chunks first so the vector block is allocated exactly once.
        // Growing a List<float> to half a gigabyte instead would spike to ~1.5GB mid-resize.
        var contributions = new List<(string RelPath, byte[] Hash, List<Chunk>? Fresh, IndexedFile? Prior)>(files.Count);
        var totalChunks = 0;

        foreach (var relPath in files)
        {
            if (!hashes.TryGetValue(relPath, out var hash))
            {
                continue;
            }

            if (freshChunks.TryGetValue(relPath, out var chunks))
            {
                contributions.Add((relPath, hash, chunks, null));
                totalChunks += chunks.Count;
            }
            else if (existing is not null && previous.TryGetValue(relPath, out var prior))
            {
                contributions.Add((relPath, hash, null, prior));
                totalChunks += prior.ChunkCount;
            }
        }

        var outFiles = new List<IndexedFile>(contributions.Count);
        var outChunks = new List<ChunkMeta>(totalChunks);
        var outVectors = new float[(long)totalChunks * dim];

        foreach (var (relPath, hash, fresh, prior) in contributions)
        {
            var start = outChunks.Count;
            var fileIndex = outFiles.Count;

            if (fresh is not null)
            {
                var vectors = freshVectors[relPath];
                for (var i = 0; i < fresh.Count; i++)
                {
                    var vector = vectors[i]
                        ?? throw new InvalidOperationException($"Chunk {i} of '{relPath}' was never embedded.");

                    outChunks.Add(ToMeta(fresh[i], fileIndex));
                    vector.AsSpan().CopyTo(outVectors.AsSpan((outChunks.Count - 1) * dim, dim));
                }
            }
            else if (prior is not null && existing is not null)
            {
                for (var i = 0; i < prior.ChunkCount; i++)
                {
                    var source = existing.Chunks[prior.ChunkStart + i];
                    outChunks.Add(new ChunkMeta
                    {
                        FileIndex = fileIndex,
                        Kind = source.Kind,
                        Symbol = source.Symbol,
                        Signature = source.Signature,
                        Namespace = source.Namespace,
                        LexicalText = source.LexicalText,
                        StartLine = source.StartLine,
                        EndLine = source.EndLine,
                    });

                    existing.VectorAt(prior.ChunkStart + i)
                        .CopyTo(outVectors.AsSpan((outChunks.Count - 1) * dim, dim));
                }
            }

            if (outChunks.Count == start)
            {
                continue;
            }

            outFiles.Add(new IndexedFile
            {
                RelPath = relPath,
                Hash = hash,
                ChunkStart = start,
                ChunkCount = outChunks.Count - start,
            });
        }

        return (outFiles, outChunks, outVectors);
    }

    private static ChunkMeta ToMeta(Chunk chunk, int fileIndex) => new()
    {
        FileIndex = fileIndex,
        Kind = chunk.Kind,
        Symbol = chunk.Symbol,
        Signature = chunk.Signature,
        Namespace = chunk.Namespace,
        LexicalText = CanonicalIndexText.Normalize(chunk.EmbedText),
        StartLine = chunk.StartLine,
        EndLine = chunk.EndLine,
    };
}
