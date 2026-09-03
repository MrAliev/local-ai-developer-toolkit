using System.Diagnostics;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using CodeSearch.Mcp;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class SearchEmbeddingFallbackTests : IDisposable
{
    private const string CalibratedModel = "qwen3-embedding:8b-q8_0";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-fallback-" + Guid.NewGuid().ToString("N"));

    /// <summary>A runtime of this test's own, so a concurrent sync cannot compete with it.</summary>
    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "codesearch-fallback-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly WorkingIndexIdentity _identity;
    private readonly GenerationIdentity _generation;

    public SearchEmbeddingFallbackTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "Alpha.cs"),
            "first line\r\nsecond line\r\nthird line\r\n");
        File.WriteAllText(
            Path.Combine(_root, "Other.cs"),
            "other line\r\n");
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@local.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("add", ".");
        Git("commit", "-m", "Initial");

        _identity = RuntimeIndexLayout.Inspect(_root, _runtimeRoot);
        _generation = Generation(CalibratedModel);
        PublishIndex(_generation, CreateIndex(_generation));
    }

    [Fact]
    public async Task Service_uses_deliberate_lexical_only_search_when_embeddings_are_unavailable()
    {
        var service = ServiceThrowing(
            new EmbeddingUnavailableException(
                "broker unavailable",
                new TimeoutException()));

        var outcome = await service.SearchAsync(
            "ExactSymbol",
            _root,
            new SearchOptions
            {
                TopK = 1,
                PathContains = "Alpha",
                MaxPerFile = 1,
                SnippetLines = 1
            },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.EmbeddingsUsed);
        var hit = Assert.Single(outcome.Hits);
        Assert.Equal("Alpha.cs", hit.RelPath);
        Assert.Equal("Example.ExactSymbol", hit.Symbol);
        Assert.Equal(0, hit.VectorScore);
        Assert.True(hit.LexicalScore > 0);
        Assert.Equal("first line\n    ...", hit.Snippet);
        Assert.NotEmpty(hit.ChunkId);
    }

    [Fact]
    public async Task Lexical_only_search_returns_only_positive_lexical_hits()
    {
        var service = ServiceThrowing(
            new EmbeddingUnavailableException(
                "broker unavailable",
                new TimeoutException()));

        var outcome = await service.SearchAsync(
            "NoSuchIdentifier",
            _root,
            new SearchOptions(),
            TestContext.Current.CancellationToken);

        Assert.Empty(outcome.Hits);
        Assert.False(outcome.EmbeddingsUsed);
    }

    [Fact]
    public async Task Service_propagates_unavailability_when_lexical_fallback_is_disabled()
    {
        var unavailable = new EmbeddingUnavailableException(
            "broker unavailable",
            new TimeoutException());

        var error = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => ServiceThrowing(unavailable).SearchAsync(
                "ExactSymbol",
                _root,
                new SearchOptions
                {
                    AllowLexicalFallbackWhenEmbeddingsUnavailable = false
                },
                TestContext.Current.CancellationToken));

        Assert.Same(unavailable, error);
    }

    [Fact]
    public async Task Mcp_returns_lexical_results_when_embeddings_are_unavailable()
    {
        var response = await CodeSearchTools.SearchCode(
            ServiceThrowing(
                new EmbeddingUnavailableException(
                    "broker unavailable",
                    new TimeoutException())),
            "ExactSymbol",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("search_code failed:", response, StringComparison.Ordinal);
        Assert.Contains("Example.ExactSymbol", response, StringComparison.Ordinal);
        Assert.Contains("cos=0.000", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fallback is deliberate and worth keeping — lexical hits beat an exception. What was
    /// missing is that the answer said nothing about it, so a caller could not tell a search
    /// that matched by meaning from one that matched the words in the sentence it was given.
    /// </summary>
    [Fact]
    public async Task Mcp_says_that_nothing_was_embedded()
    {
        var response = await CodeSearchTools.SearchCode(
            ServiceThrowing(
                new EmbeddingUnavailableException(
                    "broker unavailable",
                    new TimeoutException())),
            "ExactSymbol",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("LEXICAL ONLY", response, StringComparison.Ordinal);
        Assert.Contains(
            "matches by meaning are missing",
            response,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The header names the embedding model out of the index snapshot, which is a fact about the
    /// index rather than about this search. Printed unqualified after nothing was embedded, it
    /// is the wrong kind of true: it reports a comparison that was never made.
    /// </summary>
    [Fact]
    public async Task Mcp_does_not_present_the_model_as_having_answered()
    {
        var response = await CodeSearchTools.SearchCode(
            ServiceThrowing(
                new EmbeddingUnavailableException(
                    "broker unavailable",
                    new TimeoutException())),
            "ExactSymbol",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain($"model {CalibratedModel}", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worst case, and the likeliest one: a plain-language query has almost no literal
    /// identifiers to match, so the lexical pass finds nothing and the caller is told "No
    /// matches." — which reads as "there is no such code in this repository" rather than as
    /// "the half of this tool that answers your kind of question did not run".
    /// </summary>
    [Fact]
    public async Task An_empty_result_still_says_why_it_is_empty()
    {
        var response = await CodeSearchTools.SearchCode(
            ServiceThrowing(
                new EmbeddingUnavailableException(
                    "broker unavailable",
                    new TimeoutException())),
            "where is payment cancellation handled",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("LEXICAL ONLY", response, StringComparison.Ordinal);
        Assert.NotEqual("No matches.", response);
    }

    /// <summary>
    /// And a healthy search says none of it: a warning on every answer is a warning nobody reads.
    /// </summary>
    [Fact]
    public async Task A_search_that_embedded_normally_carries_no_warning()
    {
        var response = await CodeSearchTools.SearchCode(
            new SearchService(
                model => new StubEmbeddingClient(model),
                runtimeRoot: _runtimeRoot),
            "ExactSymbol",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("LEXICAL ONLY", response, StringComparison.Ordinal);
        Assert.Contains($"model {CalibratedModel}", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_and_unrelated_failures_are_not_treated_as_unavailability()
    {
        var cancellation = new OperationCanceledException("cancelled");
        var unrelated = new InvalidOperationException("bug");

        var cancellationError = await Assert.ThrowsAsync<OperationCanceledException>(
            () => ServiceThrowing(cancellation).SearchAsync(
                "ExactSymbol",
                _root,
                new SearchOptions(),
                TestContext.Current.CancellationToken));
        var unrelatedError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ServiceThrowing(unrelated).SearchAsync(
                "ExactSymbol",
                _root,
                new SearchOptions(),
                TestContext.Current.CancellationToken));
        var mcpResponse = await CodeSearchTools.SearchCode(
            ServiceThrowing(unrelated),
            "ExactSymbol",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(cancellation, cancellationError);
        Assert.Same(unrelated, unrelatedError);
        // The type as well as the message: an exception whose message names nothing used to
        // reach the caller as "search_code failed:" and nothing else. What this test is about is that
        // an unrelated failure is not dressed up as embeddings being unavailable, and it still
        // is not.
        Assert.Equal("search_code failed: InvalidOperationException: bug", mcpResponse);
        Assert.DoesNotContain("unavailable", mcpResponse, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_model_fails_closed_before_requesting_an_embedding()
    {
        var unknownGeneration = Generation("unknown-embedding-model");
        PublishIndex(unknownGeneration, CreateIndex(unknownGeneration));
        var embeddingRequested = false;
        var service = new SearchService(
            model =>
            {
                embeddingRequested = true;
                return new ThrowingEmbeddingClient(
                    model,
                    new InvalidOperationException("must not embed"));
            },
            runtimeRoot: _runtimeRoot);

        var error = await Assert.ThrowsAsync<SearchNotReadyException>(
            () => service.SearchAsync(
                "ExactSymbol",
                _root,
                new SearchOptions(),
                TestContext.Current.CancellationToken));

        Assert.False(embeddingRequested);
        Assert.Contains(
            "threshold not calibrated",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private SearchService ServiceThrowing(Exception exception) =>
        new(
            model => new ThrowingEmbeddingClient(model, exception),
            runtimeRoot: _runtimeRoot);

    private GenerationIdentity Generation(string model) =>
        new(
            _identity.RepositoryId,
            _identity.HeadCommit,
            _identity.HeadTree,
            model,
            2,
            2,
            CodeIndex.CurrentVersion,
            2,
            2);

    private CodeIndex CreateIndex(GenerationIdentity generation) =>
        new()
        {
            Dim = 2,
            Model = generation.EmbeddingModel,
            Root = _root,
            GitCommit = _identity.HeadCommit,
            GitTree = _identity.HeadTree,
            RepositoryId = _identity.RepositoryId,
            GenerationId = generation.Id,
            DirtyHash = null,
            IndexedAtUtc = DateTime.UtcNow,
            Files =
            [
                new IndexedFile
                {
                    RelPath = "Alpha.cs",
                    Hash = new byte[32],
                    ChunkStart = 0,
                    ChunkCount = 1
                },
                new IndexedFile
                {
                    RelPath = "Other.cs",
                    Hash = new byte[32],
                    ChunkStart = 1,
                    ChunkCount = 1
                }
            ],
            Chunks =
            [
                Meta(0, "Example.ExactSymbol", 1, 3),
                Meta(1, "Example.Unrelated", 1, 1)
            ],
            Vectors = [1f, 0f, 0f, 1f]
        };

    private static ChunkMeta Meta(
        int fileIndex,
        string symbol,
        int startLine,
        int endLine) =>
        new()
        {
            FileIndex = fileIndex,
            Kind = ChunkKind.Method,
            Symbol = symbol,
            Signature = "void " + symbol.Split('.').Last() + "()",
            Namespace = "Example",
            StartLine = startLine,
            EndLine = endLine
        };

    private void PublishIndex(
        GenerationIdentity generation,
        CodeIndex index)
    {
        var sourceIndex = Path.Combine(
            Path.GetTempPath(),
            generation.Id + "-" + Guid.NewGuid().ToString("N") + ".cidx");
        try
        {
            index.Save(sourceIndex);
            var store = new GenerationStore(_identity.RepositoryRuntimeRoot);
            var manifest = store.PublishIndex(sourceIndex, generation);
            store.SetCurrent(manifest);
        }
        finally
        {
            File.Delete(sourceIndex);
        }
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        DeleteTree(_runtimeRoot);
        DeleteTree(_root);
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// An embedder that answers, so the healthy case can be told from the degraded one. Two
    /// components, because that is the fixture index's dimension.
    /// </summary>
    private sealed class StubEmbeddingClient(string model) : IEmbeddingClient
    {
        public string Model { get; } = model;

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<float[][]>([[1f, 0f]]);
    }

    private sealed class ThrowingEmbeddingClient(
        string model,
        Exception exception) : IEmbeddingClient
    {
        public string Model { get; } = model;

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            Task.FromException<float[][]>(exception);
    }
}
