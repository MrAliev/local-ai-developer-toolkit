using CodeSearch.Core.Search;

namespace CodeSearch.Tests;

public sealed class SearchEvaluationTests
{
    [Fact]
    public void Committed_corpus_has_24_unique_source_verified_cases()
    {
        var root = RepositoryRoot();
        var corpus = SearchEvaluationCorpus.Load(
            Path.Combine(
                root,
                "tests",
                "CodeSearch.Tests",
                "Fixtures",
                "SearchEvaluation",
                "cases.json"));

        Assert.Equal(24, corpus.Cases.Count);
        Assert.Equal(
            corpus.Cases.Count,
            corpus.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        SearchEvaluationCorpus.ValidateAgainstSource(corpus, root);
    }

    [Fact]
    public void Corpus_rejects_duplicate_ids()
    {
        var corpus = new SearchEvaluationCorpus(
            1,
            [
                Case("duplicate", "first query", "src/First.cs", "First"),
                Case("duplicate", "second query", "src/Second.cs", "Second")
            ]);

        var error = Assert.Throws<InvalidDataException>(
            () => SearchEvaluationCorpus.Validate(corpus));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Corpus_rejects_inconsistent_no_answer_targets(
        bool noAnswer,
        bool hasRelevantTarget)
    {
        var relevant = hasRelevantTarget
            ? new[] { new SearchEvaluationTarget("src/Thing.cs", "Thing") }
            : [];
        var corpus = new SearchEvaluationCorpus(
            1,
            [new SearchEvaluationCase("case", "query", "intent", noAnswer, relevant)]);

        Assert.Throws<InvalidDataException>(
            () => SearchEvaluationCorpus.Validate(corpus));
    }

    [Fact]
    public void Source_validation_rejects_missing_expected_symbol()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"codesearch-evaluation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Actual.cs"), "public sealed class Actual {}");
        var corpus = new SearchEvaluationCorpus(
            1,
            [Case("source", "find expected", "Actual.cs", "Missing")]);

        try
        {
            var error = Assert.Throws<InvalidDataException>(
                () => SearchEvaluationCorpus.ValidateAgainstSource(corpus, root));
            Assert.Contains("Missing", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Metrics_are_macro_averaged_and_count_no_answer_false_positives()
    {
        var corpus = new SearchEvaluationCorpus(
            1,
            [
                Case("one", "first", "A.cs", "A"),
                new SearchEvaluationCase(
                    "two",
                    "second",
                    "intent",
                    false,
                    [
                        new SearchEvaluationTarget("B.cs", "B"),
                        new SearchEvaluationTarget("C.cs", "C")
                    ]),
                new SearchEvaluationCase("none", "unrelated", "no-answer", true, [])
            ]);
        var observations = new[]
        {
            new SearchEvaluationObservation(
                "one",
                [
                    Hit("X.cs", "X", 1, 2, 12),
                    Hit("A.cs", "A", 10, 12, 20)
                ],
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(2)),
            new SearchEvaluationObservation(
                "two",
                [
                    Hit("B.cs", "B", 1, 4, 16),
                    Hit("Y.cs", "Y", 1, 1, 4),
                    Hit("C.cs", "C", 8, 9, 8)
                ],
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(4)),
            new SearchEvaluationObservation(
                "none",
                [Hit("Noise.cs", "Noise", 1, 5, 24)],
                TimeSpan.FromMilliseconds(30),
                null)
        };

        var metrics = SearchEvaluation.Measure(corpus, observations);

        Assert.Equal(0.3, metrics.PrecisionAt5, 6);
        Assert.Equal(1.0, metrics.RecallAt10, 6);
        Assert.Equal(1.5, metrics.MeanFirstRelevantRank, 6);
        Assert.Equal(1.0, metrics.NoAnswerFalsePositiveRate, 6);
        Assert.Equal(84, metrics.ResponseCharacters);
        Assert.Equal(21, metrics.EstimatedResponseTokens);
        Assert.Equal(14, metrics.EstimatedResponseTokensLowerBound);
        Assert.Equal(28, metrics.EstimatedResponseTokensUpperBound);
        Assert.Equal(17, metrics.SourceLines);
        Assert.Equal(6, metrics.ChunkReads);
        Assert.Equal(6, metrics.FileReads);
        Assert.Equal(60, metrics.ElapsedMilliseconds, 6);
        Assert.Equal(6, metrics.BrokerQueueWaitMilliseconds);
    }

    [Fact]
    public void Token_proxy_rounds_up_and_keeps_explicit_bounds()
    {
        var estimate = SearchEvaluation.EstimateTokens(13);

        Assert.Equal(4, estimate.Point);
        Assert.Equal(3, estimate.LowerBound);
        Assert.Equal(5, estimate.UpperBound);
    }

    [Fact]
    public void Precision_counts_a_relevant_target_once_when_multiple_chunks_match_it()
    {
        var corpus = new SearchEvaluationCorpus(
            1,
            [
                new SearchEvaluationCase(
                    "answer",
                    "query",
                    "generic-text",
                    false,
                    [new SearchEvaluationTarget("README.md", null)]),
                new SearchEvaluationCase("none", "unrelated", "no-answer", true, [])
            ]);
        var observations = new[]
        {
            new SearchEvaluationObservation(
                "answer",
                [
                    Hit("README.md", "README.md [1/2]", 1, 10, 20),
                    Hit("README.md", "README.md [2/2]", 11, 20, 20)
                ],
                TimeSpan.Zero,
                null),
            new SearchEvaluationObservation(
                "none",
                [],
                TimeSpan.Zero,
                null)
        };

        var metrics = SearchEvaluation.Measure(corpus, observations);

        Assert.Equal(0.2, metrics.PrecisionAt5, 6);
        Assert.Equal(1.0, metrics.RecallAt10, 6);
    }

    [Fact]
    public void Search_hits_are_converted_to_stable_evaluation_payloads()
    {
        var hit = new SearchHit(
            "src/Thing.cs",
            4,
            7,
            CodeSearch.Core.Chunking.ChunkKind.Method,
            "Thing.Run",
            "public void Run()",
            "Example",
            0.75f,
            3,
            0.01,
            "line one\nline two");

        var evaluationHit = SearchEvaluation.FromSearchHit(hit);

        Assert.Equal(hit.RelPath, evaluationHit.Path);
        Assert.Equal(hit.Symbol, evaluationHit.Symbol);
        Assert.Equal(hit.StartLine, evaluationHit.StartLine);
        Assert.Equal(hit.EndLine, evaluationHit.EndLine);
        Assert.Equal(hit.VectorScore, evaluationHit.VectorScore);
        Assert.Equal(hit.LexicalScore, evaluationHit.LexicalScore);
        Assert.Equal(hit.Score, evaluationHit.FusedScore);
        Assert.Equal(
            SearchEvaluation.RenderHit(hit).Length,
            evaluationHit.ResponseCharacters);
    }

    [Fact]
    public void Calibrated_profile_records_the_measured_threshold_and_provenance()
    {
        var profile = SearchQualityProfile.Require("qwen3-embedding:8b-q8_0");

        Assert.Equal(0.43f, profile.MinVectorScore);
        Assert.Equal(24, profile.CaseCount);
        Assert.Equal(
            "399fcc0b53b35ede05dc64f1a84cbc3bfc6bf382bdd2de7d71f2f9dc1ae8debc",
            profile.GenerationId);
        Assert.Contains("no-answer", profile.SelectionRule);
        Assert.Contains("recall@10", profile.SelectionRule);
    }

    [Fact]
    public void Unknown_model_fails_closed_without_evaluation_opt_out()
    {
        var error = Assert.Throws<SearchNotReadyException>(
            () => SearchQualityProfile.Resolve(
                "unknown-embedding-model",
                new SearchOptions()));

        Assert.Contains(
            "threshold not calibrated",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluation_can_explicitly_opt_out_for_an_unknown_model()
    {
        var options = SearchQualityProfile.Resolve(
            "unknown-embedding-model",
            new SearchOptions
            {
                AllowUncalibratedModelForEvaluation = true
            });

        Assert.Null(options.MinVectorScore);
    }

    private static SearchEvaluationCase Case(
        string id,
        string query,
        string path,
        string symbol) =>
        new(
            id,
            query,
            "intent",
            false,
            [new SearchEvaluationTarget(path, symbol)]);

    private static SearchEvaluationHit Hit(
        string path,
        string symbol,
        int startLine,
        int endLine,
        int responseCharacters) =>
        new(path, symbol, startLine, endLine, responseCharacters);

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LocalAi.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate LocalAi.slnx.");
    }
}
