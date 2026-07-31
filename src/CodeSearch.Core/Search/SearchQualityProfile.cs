using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CodeSearch.Core.Search;

public sealed record SearchQualityProfile(
    string Model,
    float MinVectorScore,
    string CorpusVersion,
    string GenerationId,
    int CaseCount,
    DateTimeOffset CalibratedAtUtc,
    string SelectionRule)
{
    private static readonly IReadOnlyDictionary<string, SearchQualityProfile> Profiles =
        new Dictionary<string, SearchQualityProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["qwen3-embedding:8b-q8_0"] = new(
                "qwen3-embedding:8b-q8_0",
                0.43f,
                "schema1:sha256:d675331cb7008a67a7335c5a1f2aba85e382974b71b1473e34b9e4685f0d7a52",
                "399fcc0b53b35ede05dc64f1a84cbc3bfc6bf382bdd2de7d71f2f9dc1ae8debc",
                24,
                new DateTimeOffset(2026, 7, 30, 23, 55, 0, TimeSpan.Zero),
                "Lowest conservative hundredth above every observed no-answer vector score " +
                "(maximum 0.426) without excluding an observed relevant score " +
                "(minimum 0.508); recall@10 is therefore not reduced by the floor.")
        };

    public static SearchQualityProfile Require(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (Profiles.TryGetValue(model, out var profile))
        {
            return profile;
        }

        throw new SearchNotReadyException(
            $"Semantic relevance threshold not calibrated for embedding model '{model}'.");
    }

    public static SearchOptions Resolve(string model, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowUncalibratedModelForEvaluation)
        {
            return options;
        }

        var profile = Require(model);
        if (options.MinVectorScore is { } requested &&
            requested != profile.MinVectorScore)
        {
            throw new SearchNotReadyException(
                $"MinVectorScore {requested} does not match the calibrated threshold " +
                $"{profile.MinVectorScore} for '{model}'. Only evaluation may opt out.");
        }

        return options with { MinVectorScore = profile.MinVectorScore };
    }
}

public sealed record SearchEvaluationTarget(
    string Path,
    string? Symbol);

public sealed record SearchEvaluationCase(
    string Id,
    string Query,
    string Category,
    bool NoAnswer,
    IReadOnlyList<SearchEvaluationTarget> Relevant);

public sealed record SearchEvaluationCorpus(
    int SchemaVersion,
    IReadOnlyList<SearchEvaluationCase> Cases)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static SearchEvaluationCorpus Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var corpus = JsonSerializer.Deserialize<SearchEvaluationCorpus>(
            File.ReadAllText(path),
            JsonOptions)
            ?? throw new InvalidDataException($"Evaluation corpus '{path}' is empty.");
        Validate(corpus);
        return corpus;
    }

    public static void Validate(SearchEvaluationCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        if (corpus.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported evaluation corpus schema {corpus.SchemaVersion}; expected 1.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in corpus.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Query) ||
                string.IsNullOrWhiteSpace(item.Category))
            {
                throw new InvalidDataException(
                    "Every evaluation case needs a non-blank id, query, and category.");
            }

            if (!ids.Add(item.Id))
            {
                throw new InvalidDataException(
                    $"Duplicate evaluation case id '{item.Id}'.");
            }

            if (item.NoAnswer == (item.Relevant.Count > 0))
            {
                throw new InvalidDataException(
                    $"Evaluation case '{item.Id}' has inconsistent noAnswer/relevant targets.");
            }

            foreach (var target in item.Relevant)
            {
                if (string.IsNullOrWhiteSpace(target.Path))
                {
                    throw new InvalidDataException(
                        $"Evaluation case '{item.Id}' has a blank relevant path.");
                }
            }
        }
    }

    public static void ValidateAgainstSource(
        SearchEvaluationCorpus corpus,
        string root)
    {
        Validate(corpus);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = root + Path.DirectorySeparatorChar;

        foreach (var item in corpus.Cases)
        {
            foreach (var target in item.Relevant)
            {
                var path = Path.GetFullPath(
                    Path.Combine(
                        root,
                        target.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(rootPrefix, comparison) || !File.Exists(path))
                {
                    throw new InvalidDataException(
                        $"Evaluation target '{target.Path}' for '{item.Id}' is missing or outside the repository.");
                }

                if (!string.IsNullOrWhiteSpace(target.Symbol))
                {
                    var sourceName = target.Symbol.Split('.').Last();
                    if (!File.ReadAllText(path).Contains(
                            sourceName,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Evaluation target symbol '{target.Symbol}' for '{item.Id}' is absent from '{target.Path}'.");
                    }
                }
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.MakeReadOnly();
        return options;
    }
}

public sealed record SearchEvaluationHit(
    string Path,
    string Symbol,
    int StartLine,
    int EndLine,
    int ResponseCharacters,
    int SourceLines,
    float VectorScore = 0,
    double LexicalScore = 0,
    double FusedScore = 0);

public sealed record SearchEvaluationObservation(
    string CaseId,
    IReadOnlyList<SearchEvaluationHit> Hits,
    TimeSpan Elapsed,
    TimeSpan? BrokerQueueWait);

public sealed record TokenEstimate(
    int Point,
    int LowerBound,
    int UpperBound);

public sealed record SearchEvaluationMetrics(
    double PrecisionAt5,
    double RecallAt10,
    double MeanFirstRelevantRank,
    double NoAnswerFalsePositiveRate,
    int ResponseCharacters,
    int EstimatedResponseTokens,
    int EstimatedResponseTokensLowerBound,
    int EstimatedResponseTokensUpperBound,
    int SourceLines,
    int ChunkReads,
    int FileReads,
    double ElapsedMilliseconds,
    double? BrokerQueueWaitMilliseconds);

public static class SearchEvaluation
{
    public static SearchOptions CreateSearchOptions(bool noFloor) =>
        new()
        {
            TopK = 10,
            MaxPerFile = 3,
            AllowUncalibratedModelForEvaluation = noFloor,
            AllowLexicalFallbackWhenEmbeddingsUnavailable = false
        };

    public static SearchEvaluationHit FromSearchHit(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return new SearchEvaluationHit(
            hit.RelPath,
            hit.Symbol,
            hit.StartLine,
            hit.EndLine,
            RenderHit(hit).Length,
            CountSourceLines(hit.Snippet),
            hit.VectorScore,
            hit.LexicalScore,
            hit.Score);
    }

    public static string RenderHit(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hit.RelPath}:{hit.StartLine}-{hit.EndLine} [{hit.Kind}] cos={hit.VectorScore:F3}\n" +
            $"{hit.Symbol}\n{hit.Signature}\n{hit.Snippet}");
    }

    public static SearchEvaluationMetrics Measure(
        SearchEvaluationCorpus corpus,
        IReadOnlyList<SearchEvaluationObservation> observations)
    {
        SearchEvaluationCorpus.Validate(corpus);
        ArgumentNullException.ThrowIfNull(observations);

        var byCase = observations
            .GroupBy(item => item.CaseId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        var expectedIds = corpus.Cases
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (byCase.Count != expectedIds.Count ||
            byCase.Keys.Any(id => !expectedIds.Contains(id)))
        {
            throw new InvalidDataException(
                "Evaluation observations must contain every corpus case exactly once.");
        }

        var answerable = corpus.Cases.Where(item => !item.NoAnswer).ToArray();
        var noAnswer = corpus.Cases.Where(item => item.NoAnswer).ToArray();
        double precisionAt5 = 0;
        double recallAt10 = 0;
        double firstRelevantRank = 0;
        foreach (var item in answerable)
        {
            var hits = byCase[item.Id].Hits;
            var matchedAt5 = item.Relevant.Count(
                target => hits.Take(5).Any(hit => Matches(target, hit)));
            precisionAt5 += matchedAt5 / 5.0;

            var matchedTargets = item.Relevant.Count(
                target => hits.Take(10).Any(hit => Matches(target, hit)));
            recallAt10 += matchedTargets / (double)item.Relevant.Count;

            var first = hits
                .Select((hit, index) => (Hit: hit, Rank: index + 1))
                .FirstOrDefault(pair => IsRelevant(item, pair.Hit));
            firstRelevantRank += first.Rank == 0 ? 11 : first.Rank;
        }

        var allHits = observations.SelectMany(item => item.Hits).ToArray();
        var characters = allHits.Sum(item => item.ResponseCharacters);
        var estimate = EstimateTokens(characters);
        var queueWaits = observations
            .Where(item => item.BrokerQueueWait is not null)
            .Select(item => item.BrokerQueueWait!.Value.TotalMilliseconds)
            .ToArray();

        return new SearchEvaluationMetrics(
            precisionAt5 / answerable.Length,
            recallAt10 / answerable.Length,
            firstRelevantRank / answerable.Length,
            noAnswer.Count(item => byCase[item.Id].Hits.Count > 0) /
            (double)noAnswer.Length,
            characters,
            estimate.Point,
            estimate.LowerBound,
            estimate.UpperBound,
            allHits.Sum(item => item.SourceLines),
            allHits.Length,
            observations.Sum(item =>
                item.Hits.Select(hit => NormalizePath(hit.Path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()),
            observations.Sum(item => item.Elapsed.TotalMilliseconds),
            queueWaits.Length == 0 ? null : queueWaits.Sum());
    }

    public static int CountSourceLines(string snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        if (snippet.Length == 0 ||
            string.Equals(
                snippet,
                "(file changed since indexing - snippet unavailable)",
                StringComparison.Ordinal))
        {
            return 0;
        }

        const string truncationMarker = "\n    ...";
        var sourceLength = snippet.EndsWith(
            truncationMarker,
            StringComparison.Ordinal)
            ? snippet.Length - truncationMarker.Length
            : snippet.Length;
        if (sourceLength == 0)
        {
            return 0;
        }

        var lines = 1;
        for (var index = 0; index < sourceLength; index++)
        {
            if (snippet[index] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    public static TokenEstimate EstimateTokens(int characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characters);
        return new TokenEstimate(
            DivideRoundUp(characters, 4),
            DivideRoundUp(characters, 6),
            DivideRoundUp(characters, 3));
    }

    private static bool IsRelevant(
        SearchEvaluationCase item,
        SearchEvaluationHit hit) =>
        item.Relevant.Any(target => Matches(target, hit));

    private static bool Matches(
        SearchEvaluationTarget target,
        SearchEvaluationHit hit) =>
        string.Equals(
            NormalizePath(target.Path),
            NormalizePath(hit.Path),
            StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(target.Symbol) ||
         string.Equals(target.Symbol, hit.Symbol, StringComparison.Ordinal));

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private static int DivideRoundUp(int value, int divisor) =>
        (value + divisor - 1) / divisor;
}
