using System.Diagnostics;
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
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
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
    public void Snippets_refuse_a_reparse_point_component_outside_the_repository()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "codesearch-snippet-outside-" + Guid.NewGuid().ToString("N"));
        var linkPath = Path.Combine(_root, "Linked");
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(
            Path.Combine(outsideRoot, "External.cs"),
            "external secret\n");
        try
        {
            CreateDirectoryLink(linkPath, outsideRoot);
            var index = SingleFileIndex(Path.Combine("Linked", "External.cs"));

            var hit = Assert.Single(SearchEngine.Search(
                index,
                Unit(1, 0, 0),
                "external",
                new SearchOptions { TopK = 1 },
                _root));

            Assert.Equal(
                "(file changed since indexing - snippet unavailable)",
                hit.Snippet);
            Assert.DoesNotContain("external secret", hit.Snippet, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Snippets_refuse_lexical_paths_outside_the_repository()
    {
        var outsidePath = Path.Combine(
            Path.GetDirectoryName(_root)!,
            "codesearch-snippet-outside-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(outsidePath, "external lexical secret\n");
        try
        {
            var index = SingleFileIndex(Path.GetRelativePath(_root, outsidePath));

            var hit = Assert.Single(SearchEngine.Search(
                index,
                Unit(1, 0, 0),
                "external",
                new SearchOptions { TopK = 1 },
                _root));

            Assert.Equal(
                "(file changed since indexing - snippet unavailable)",
                hit.Snippet);
            Assert.DoesNotContain(
                "external lexical secret",
                hit.Snippet,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public void Missing_source_files_produce_an_unavailable_snippet()
    {
        var hit = Assert.Single(SearchEngine.Search(
            SingleFileIndex("Missing.cs"),
            Unit(1, 0, 0),
            "missing",
            new SearchOptions { TopK = 1 },
            _root));

        Assert.Equal(
            "(file changed since indexing - snippet unavailable)",
            hit.Snippet);
    }

    [Fact]
    public void Hits_carry_an_opaque_id_for_the_exact_index_snapshot()
    {
        var hit = Assert.Single(SearchEngine.Search(
            _index, Unit(1, 0, 0), "charging", new SearchOptions { TopK = 1 }, _root));

        Assert.Equal(
            new SearchChunkId("repository", "generation", "tree", null, 0),
            SearchChunkId.Parse(hit.ChunkId));
    }

    [Fact]
    public void MismatchedVectorWidthFailsLoudlyInsteadOfRankingGarbage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SearchEngine.Search(_index, [1f, 0f], "anything", new SearchOptions(), _root));

        Assert.Contains("test-model", ex.Message);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1.01f)]
    [InlineData(1.01f)]
    public void Rejects_invalid_vector_score_floors(float floor)
    {
        var options = new SearchOptions { MinVectorScore = floor };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SearchEngine.Search(
                _index,
                Unit(1, 0, 0),
                "anything",
                options,
                _root));
    }

    [Fact]
    public void Excludes_below_floor_vectors_before_ranking()
    {
        var hits = SearchEngine.Search(
            _index,
            Unit(1, 0, 0),
            "unrelated",
            new SearchOptions { TopK = 10, MinVectorScore = 0.99f },
            _root);

        var hit = Assert.Single(hits);
        Assert.Equal("PaymentService.Charge", hit.Symbol);
    }

    [Fact]
    public void Includes_a_vector_exactly_at_the_floor()
    {
        var hits = SearchEngine.Search(
            _index,
            Unit(1, 0, 0),
            "unrelated",
            new SearchOptions { TopK = 10, MinVectorScore = 1f },
            _root);

        Assert.Contains(hits, hit => hit.Symbol == "PaymentService.Charge");
    }

    [Fact]
    public void Lexical_match_survives_when_its_vector_is_below_the_floor()
    {
        var hits = SearchEngine.Search(
            _index,
            Unit(1, 0, 0),
            "where is TrustSetFlags",
            new SearchOptions { TopK = 10, MinVectorScore = 1f },
            _root);

        var lexical = Assert.Single(
            hits,
            hit => hit.Symbol == "RobotScenario.TrustSetFlags");
        Assert.Equal(0, lexical.VectorScore);
        Assert.True(lexical.LexicalScore > 0);
    }

    [Fact]
    public void No_answer_can_return_zero_hits()
    {
        var hits = SearchEngine.Search(
            _index,
            Unit(0, 1, 0),
            "unrelated",
            new SearchOptions { TopK = 10, MinVectorScore = 1f },
            _root);

        Assert.Empty(hits);
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

    private CodeIndex SingleFileIndex(string relPath) =>
        new()
        {
            Dim = 3,
            Model = "test-model",
            Root = _root,
            GitCommit = "abc123",
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            IndexedAtUtc = DateTime.UtcNow,
            Files =
            [
                new IndexedFile
                {
                    RelPath = relPath,
                    Hash = new byte[32],
                    ChunkStart = 0,
                    ChunkCount = 1
                }
            ],
            Chunks =
            [
                Meta(
                    0,
                    ChunkKind.Type,
                    "External",
                    "class External",
                    1,
                    1)
            ],
            Vectors = [1f, 0f, 0f]
        };

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(linkPath);
        start.ArgumentList.Add(targetPath);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

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
