using System.Numerics.Tensors;
using System.Text;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Indexing;

namespace CodeSearch.Core.Search;

public sealed record SearchOptions
{
    public int TopK { get; init; } = 10;
    public ChunkKind? Kind { get; init; }
    public string? PathContains { get; init; }

    /// <summary>
    /// Caps hits from any one file. Without it a single large service class can take every slot
    /// with near-identical neighbouring methods and hide the rest of the codebase.
    /// </summary>
    public int MaxPerFile { get; init; } = 3;

    public int SnippetLines { get; init; } = 12;

    /// <summary>
    /// Vector candidates below this cosine score never receive an RRF rank. Null is reserved for
    /// the explicit historical no-floor evaluation mode.
    /// </summary>
    public float? MinVectorScore { get; init; }

    /// <summary>Only the deterministic evaluator may bypass a calibrated model profile.</summary>
    public bool AllowUncalibratedModelForEvaluation { get; init; }
}

public sealed record SearchHit(
    string RelPath,
    int StartLine,
    int EndLine,
    ChunkKind Kind,
    string Symbol,
    string Signature,
    string Namespace,
    float VectorScore,
    double LexicalScore,
    double Score,
    string Snippet,
    string ChunkId = "");

/// <summary>
/// Hybrid retrieval: dense vectors for meaning, literal symbol matching for names, fused with
/// Reciprocal Rank Fusion.
///
/// Both halves are load-bearing. Pure semantics answers "where do we close an order after payment"
/// but is weak on "where is TrustSetFlags" - and in this codebase every other question is the
/// second kind. RRF is used rather than a weighted score sum because the two signals are on
/// incomparable scales (cosine ~0.4-0.9 vs an unbounded token count), and rank fusion needs no
/// per-corpus tuning to stay sane.
/// </summary>
public static class SearchEngine
{
    /// <summary>Standard RRF damping. Larger k flattens the contribution of top ranks.</summary>
    private const double RrfK = 60;

    private const int CandidatesPerSignal = 250;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "where", "which", "what", "when",
        "how", "does", "did", "are", "was", "were", "has", "have", "into", "out", "any", "all",
        "код", "где", "как", "что", "для", "это", "при", "или", "если", "файл", "класс", "метод",
    };

    /// <summary>
    /// <paramref name="root"/> is the working checkout snippets are read from - which is the
    /// caller's worktree, not the index's own root. For a base-only chunk the file is identical
    /// in both by construction, so reading from the worktree is always correct and always current.
    /// </summary>
    public static IReadOnlyList<SearchHit> Search(
        ISearchableIndex index, float[] queryVector, string queryText, SearchOptions options, string root)
    {
        Validate(options);
        if (queryVector.Length != index.Dim)
        {
            throw new InvalidOperationException(
                $"Query vector is {queryVector.Length}-dimensional but the index is {index.Dim}. " +
                $"The index was built with '{index.Model}' - query with the same model.");
        }

        var candidates = Filter(index, options);
        if (candidates.Count == 0)
        {
            return [];
        }

        var vectorScores = ScoreVectors(index, queryVector, candidates);
        var lexicalScores = ScoreLexically(index, queryText, candidates);
        var eligibleVectorScores = options.MinVectorScore is { } floor
            ? vectorScores
                .Where(pair => pair.Value >= floor)
                .ToDictionary(pair => pair.Key, pair => pair.Value)
            : vectorScores;
        var fused = Fuse(eligibleVectorScores, lexicalScores);

        return Materialize(index, root, fused, vectorScores, lexicalScores, options);
    }

    /// <summary>
    /// Searches only the literal signal when the embedding service is explicitly unavailable.
    /// A lexical-only result has no semantic score and only positive literal matches are eligible.
    /// </summary>
    public static IReadOnlyList<SearchHit> SearchLexically(
        ISearchableIndex index,
        string queryText,
        SearchOptions options,
        string root)
    {
        Validate(options);
        var candidates = Filter(index, options);
        if (candidates.Count == 0)
        {
            return [];
        }

        var lexicalScores = ScoreLexically(index, queryText, candidates);
        if (lexicalScores.Count == 0)
        {
            return [];
        }

        var vectorScores = new Dictionary<int, float>();
        var fused = Fuse(vectorScores, lexicalScores);
        return Materialize(
            index,
            root,
            fused,
            vectorScores,
            lexicalScores,
            options);
    }

    private static void Validate(SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinVectorScore is not { } floor)
        {
            return;
        }

        if (!float.IsFinite(floor) || floor is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                floor,
                "MinVectorScore must be finite and within [-1, 1].");
        }
    }

    private static List<int> Filter(ISearchableIndex index, SearchOptions options)
    {
        var candidates = new List<int>(index.ChunkCount);
        for (var i = 0; i < index.ChunkCount; i++)
        {
            if (options.Kind is { } kind && index.ChunkAt(i).Kind != kind)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(options.PathContains) &&
                index.PathOf(i).IndexOf(options.PathContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            candidates.Add(i);
        }

        return candidates;
    }

    private static Dictionary<int, float> ScoreVectors(ISearchableIndex index, float[] queryVector, List<int> candidates)
    {
        var scores = new float[candidates.Count];
        var query = queryVector;

        // Vectors are stored L2-normalized, so the dot product IS the cosine. TensorPrimitives
        // runs it on SIMD registers - tens of thousands of 2560-wide dots land in milliseconds,
        // which is why a brute-force scan beats adding an approximate ANN index here.
        Parallel.For(0, candidates.Count, i =>
        {
            scores[i] = TensorPrimitives.Dot(query.AsSpan(), index.VectorAt(candidates[i]));
        });

        var result = new Dictionary<int, float>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            result[candidates[i]] = scores[i];
        }

        return result;
    }

    private static Dictionary<int, double> ScoreLexically(ISearchableIndex index, string queryText, List<int> candidates)
    {
        var tokens = Tokenize(queryText);
        var scores = new Dictionary<int, double>();
        if (tokens.Count == 0)
        {
            return scores;
        }

        foreach (var chunkIndex in candidates)
        {
            var chunk = index.ChunkAt(chunkIndex);
            var relPath = index.PathOf(chunkIndex);

            double score = 0;
            foreach (var token in tokens)
            {
                // A hit on the symbol name is the strongest signal a literal query can give;
                // the path is the weakest but still separates same-named types across modules.
                if (chunk.Symbol.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                }
                else if (chunk.Signature.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 2;
                }
                else if (relPath.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1;
                }
            }

            if (score > 0)
            {
                scores[chunkIndex] = score;
            }
        }

        return scores;
    }

    /// <summary>
    /// Pulls identifier-ish terms out of a query, including the parts of a CamelCase name, so
    /// "GetRobotScenarioState" also matches chunks that only mention Robot or Scenario.
    /// </summary>
    public static List<string> Tokenize(string query)
    {
        var raw = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                raw.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            raw.Add(current.ToString());
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in raw)
        {
            Add(tokens, word);

            // Underscores first, then CamelCase within each part: an identifier can mix both
            // conventions (order_PaymentConfirmed), and snake_case names are everywhere in SQL,
            // Python and integration event names.
            foreach (var part in word.Split('_', StringSplitOptions.RemoveEmptyEntries))
            {
                Add(tokens, part);
                foreach (var camel in SplitCamelCase(part))
                {
                    Add(tokens, camel);
                }
            }
        }

        return tokens.ToList();

        static void Add(HashSet<string> tokens, string token)
        {
            if (token.Length >= 3 && !StopWords.Contains(token))
            {
                tokens.Add(token);
            }
        }
    }

    private static IEnumerable<string> SplitCamelCase(string word)
    {
        var start = 0;
        for (var i = 1; i < word.Length; i++)
        {
            if (char.IsUpper(word[i]) && !char.IsUpper(word[i - 1]))
            {
                yield return word[start..i];
                start = i;
            }
        }

        if (start > 0)
        {
            yield return word[start..];
        }
    }

    private static List<int> Fuse(
        Dictionary<int, float> vectorScores,
        Dictionary<int, double> lexicalScores)
    {
        var fused = new Dictionary<int, double>();

        var byVector = vectorScores
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Key)
            .Take(CandidatesPerSignal);

        var rank = 0;
        foreach (var chunkIndex in byVector)
        {
            fused[chunkIndex] = fused.GetValueOrDefault(chunkIndex) + 1.0 / (RrfK + ++rank);
        }

        rank = 0;
        foreach (var chunkIndex in lexicalScores.OrderByDescending(kv => kv.Value).Take(CandidatesPerSignal).Select(kv => kv.Key))
        {
            fused[chunkIndex] = fused.GetValueOrDefault(chunkIndex) + 1.0 / (RrfK + ++rank);
        }

        return fused.OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => vectorScores.GetValueOrDefault(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
    }

    private static List<SearchHit> Materialize(
        ISearchableIndex index,
        string root,
        List<int> ordered,
        Dictionary<int, float> vectorScores,
        Dictionary<int, double> lexicalScores,
        SearchOptions options)
    {
        var hits = new List<SearchHit>(options.TopK);
        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var rrf = new Dictionary<int, double>();

        var position = 0;
        foreach (var chunkIndex in ordered)
        {
            rrf[chunkIndex] = 1.0 / (RrfK + ++position);
        }

        foreach (var chunkIndex in ordered)
        {
            if (hits.Count >= options.TopK)
            {
                break;
            }

            var chunk = index.ChunkAt(chunkIndex);
            var relPath = index.PathOf(chunkIndex);

            var used = perFile.GetValueOrDefault(relPath);
            if (options.MaxPerFile > 0 && used >= options.MaxPerFile)
            {
                continue;
            }

            perFile[relPath] = used + 1;

            hits.Add(new SearchHit(
                relPath,
                chunk.StartLine,
                chunk.EndLine,
                chunk.Kind,
                chunk.Symbol,
                chunk.Signature,
                chunk.Namespace,
                vectorScores.GetValueOrDefault(chunkIndex),
                lexicalScores.GetValueOrDefault(chunkIndex),
                rrf[chunkIndex],
                Snippet(root, relPath, chunk, options.SnippetLines, fileCache),
                new SearchChunkId(
                    index.RepositoryId,
                    index.GenerationId,
                    index.GitTree,
                    index.DirtyHash,
                    chunkIndex).Encode()));
        }

        return hits;
    }

    /// <summary>
    /// Snippets are read from the working tree, never stored in the index. That keeps the index to
    /// vectors plus metadata, and means shown code always matches the file on disk even when the
    /// index itself has drifted behind HEAD.
    /// </summary>
    private static string Snippet(
        string root, string relPath, ChunkMeta chunk, int maxLines, Dictionary<string, string[]> cache)
    {
        if (!cache.TryGetValue(relPath, out var lines))
        {
            if (!SafeSourcePath.TryResolveFile(
                    root,
                    relPath,
                    out var fullPath,
                    out _))
            {
                lines = [];
            }
            else
            {
                try
                {
                    lines = SourceLines.Split(File.ReadAllText(fullPath));
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException)
                {
                    lines = [];
                }
            }

            cache[relPath] = lines;
        }

        if (lines.Length == 0 || chunk.StartLine > lines.Length)
        {
            return "(file changed since indexing - snippet unavailable)";
        }

        var start = Math.Max(0, chunk.StartLine - 1);
        var end = Math.Min(lines.Length, Math.Min(chunk.EndLine, start + maxLines));
        var body = string.Join("\n", lines[start..end]);

        return end < chunk.EndLine ? body + "\n    ..." : body;
    }
}
