using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;

namespace CodeSearch.Tests;

public class SearchEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codesearch-root-{Guid.NewGuid():N}");
    private readonly CodeIndex _index;

    public SearchEngineTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Payments.cs"),
            string.Join('\n', Enumerable.Range(1, 40).Select(i => $"// payments line {i}")));
        File.WriteAllText(Path.Combine(_root, "Robot.cs"),
            string.Join('\n', Enumerable.Range(1, 40).Select(i => $"// robot line {i}")));

        // Vector space is deliberately trivial: "payment-ish" is the X axis, "robot-ish" is Z.
        _index = new CodeIndex
        {
            Dim = 3,
            Model = "test-model",
            Root = _root,
            GitCommit = "abc123",
            IndexedAtUtc = DateTime.UtcNow,
            Files =
            [
                new IndexedFile { RelPath = "Payments.cs", Hash = new byte[32], ChunkStart = 0, ChunkCount = 3 },
                new IndexedFile { RelPath = "Robot.cs", Hash = new byte[32], ChunkStart = 3, ChunkCount = 1 },
            ],
            Chunks =
            [
                Meta(0, ChunkKind.Method, "PaymentService.Charge", "public Task Charge()", 1, 10),
                Meta(0, ChunkKind.Method, "PaymentService.Refund", "public Task Refund()", 11, 20),
                Meta(0, ChunkKind.Type, "PaymentService", "public class PaymentService", 1, 40),
                Meta(1, ChunkKind.Method, "RobotScenario.TrustSetFlags", "public void TrustSetFlags()", 5, 12),
            ],
            Vectors = Normalized(
                [1f, 0f, 0f],
                [0.95f, 0.30f, 0f],
                [0.90f, 0.40f, 0f],
                [0f, 0f, 1f]),
        };
    }

    [Fact]
    public void RanksBySemanticSimilarityWhenTheQueryNamesNothing()
    {
        var hits = SearchEngine.Search(
            _index, Unit(1, 0, 0), "charging a customer", new SearchOptions { TopK = 3 }, _root);

        Assert.Equal("PaymentService.Charge", hits[0].Symbol);
        Assert.True(hits[0].VectorScore > 0.99f);
    }

    [Fact]
    public void ExactSymbolNameSurfacesEvenWhenTheVectorPointsElsewhere()
    {
        // This is the case pure semantic search loses: the query names a symbol, but the query
        // embedding sits in the wrong region entirely. Without the lexical half, TrustSetFlags
        // would rank last.
        var hits = SearchEngine.Search(
            _index, Unit(1, 0, 0), "where is TrustSetFlags", new SearchOptions { TopK = 2 }, _root);

        Assert.Contains(hits, h => h.Symbol == "RobotScenario.TrustSetFlags");
        Assert.True(hits.Single(h => h.Symbol == "RobotScenario.TrustSetFlags").LexicalScore > 0);
    }

    [Fact]
    public void MaxPerFileStopsOneClassTakingEverySlot()
    {
        var hits = SearchEngine.Search(
            _index, Unit(1, 0, 0), "payment", new SearchOptions { TopK = 5, MaxPerFile = 1 }, _root);

        Assert.Single(hits, h => h.RelPath == "Payments.cs");
    }

    [Fact]
    public void FiltersByKindAndPath()
    {
        var byKind = SearchEngine.Search(
            _index, Unit(1, 0, 0), "payment", new SearchOptions { TopK = 10, Kind = ChunkKind.Type }, _root);
        Assert.All(byKind, h => Assert.Equal(ChunkKind.Type, h.Kind));

        var byPath = SearchEngine.Search(
            _index, Unit(1, 0, 0), "anything", new SearchOptions { TopK = 10, PathContains = "Robot" }, _root);
        Assert.All(byPath, h => Assert.Contains("Robot", h.RelPath));
    }

    [Fact]
    public void SnippetsComeFromDiskAtTheChunkLineRange()
    {
        var hits = SearchEngine.Search(
            _index, Unit(1, 0, 0), "charging", new SearchOptions { TopK = 1 }, _root);

        Assert.Contains("payments line 1", hits[0].Snippet);
        Assert.DoesNotContain("payments line 20", hits[0].Snippet);
    }

    [Fact]
    public void MismatchedVectorWidthFailsLoudlyInsteadOfRankingGarbage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SearchEngine.Search(_index, [1f, 0f], "anything", new SearchOptions(), _root));

        Assert.Contains("test-model", ex.Message);
    }

    [Theory]
    [InlineData("GetRobotScenarioState", "Robot")]
    [InlineData("GetRobotScenarioState", "Scenario")]
    [InlineData("order_payment_confirmed", "payment")]
    public void TokenizerSplitsCompoundIdentifiers(string query, string expected)
    {
        Assert.Contains(expected, SearchEngine.Tokenize(query), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TokenizerDropsNoiseWords()
    {
        var tokens = SearchEngine.Tokenize("where does the code that handles refunds live");

        Assert.DoesNotContain("the", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("where", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("refunds", tokens, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp dir must not fail the suite.
        }
    }

    private static ChunkMeta Meta(int file, ChunkKind kind, string symbol, string signature, int start, int end) =>
        new()
        {
            FileIndex = file,
            Kind = kind,
            Symbol = symbol,
            Signature = signature,
            Namespace = "Test",
            StartLine = start,
            EndLine = end,
        };

    private static float[] Unit(params float[] values)
    {
        EmbeddingVector.Normalize(values);
        return values;
    }

    private static float[] Normalized(params float[][] vectors)
    {
        var flat = new List<float>();
        foreach (var vector in vectors)
        {
            EmbeddingVector.Normalize(vector);
            flat.AddRange(vector);
        }

        return flat.ToArray();
    }
}
