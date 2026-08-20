using CodeSearch.Core.Chunking;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using CodeSearch.Core.Semantics;
using LocalAi.Cli;
using LocalAi.TestSupport;

namespace LocalAi.IntegrationTests;

/// <summary>
/// Chunks a generated repository with the real external indexer and checks the shape of what
/// comes out.
/// </summary>
/// <remarks>
/// The corpus that measured symbol-aware chunking belongs to somebody else's repository, so the
/// numbers in `docs/codesearch-evaluation.md` are committed and the cases are not, and nothing
/// there can be re-run here. This is the half of that measurement which can live in CI, and the
/// half worth having there: retrieval scores need an embedding model, which a runner does not
/// have, but the shape of the corpus needs nothing but the chunker and `scip-typescript`, and it
/// is the shape that a change to chunking breaks.
///
/// The invariants below are the ones stated in <see cref="SymbolAwareChunker"/> as prose. Stated
/// against a generated repository of realistic files, they fail on a boundary that moves — which
/// is what "before and after" means for this part of the system.
/// </remarks>
public sealed class SyntheticCorpusChunkingTests : IDisposable
{
    /// <summary>
    /// Ten features is every shape the chunker distinguishes, and every case the committed corpus
    /// names. More features make retrieval harder and chunking no different, so the expensive
    /// dimension is left to whoever is measuring retrieval rather than paid for on every run.
    /// </summary>
    private const int Features = 10;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-synthetic-corpus-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Cuts_the_generated_repository_on_its_declarations()
    {
        var chunks = await ChunkAsync();

        // The shape the whole of #103 was about: an initialiser that is a call, which the indexer
        // names and reports no body for.
        var card = chunks.Single(chunk =>
            chunk.RelPath.Replace('\\', '/') == "src/features/invoice/ui/InvoiceCard.tsx" &&
            chunk.Symbol == "InvoiceCard");
        Assert.Contains("memo(", card.Signature, StringComparison.Ordinal);
        Assert.Contains("handleOpen", card.EmbedText, StringComparison.Ordinal);

        // A reported body, for contrast, and a Python one where the indexer is present.
        Assert.Contains(chunks, chunk => chunk.Symbol == "shouldRetryRequest");
        Assert.Contains(chunks, chunk => chunk.Symbol == "visibleMenu");

        // An object literal whose every property the indexer names. One chunk for the literal,
        // none for its properties: a vector per endpoint is the corpus bloat this avoids.
        Assert.Contains(chunks, chunk => chunk.Symbol == "invoiceApi");
        Assert.DoesNotContain(chunks, chunk =>
            chunk.Symbol is "list" or "byId" or "create" or "update" or "remove");

        // A declaration that ends where it starts stays in the region it is written in — and the
        // comment documenting the next declaration must not lengthen it into one that does not.
        Assert.DoesNotContain(chunks, chunk => chunk.Symbol is "DASH" or "STORAGE_KEY");

        // The prose that answers "why is the token not in localStorage" has to travel with the
        // function it describes, or the corpus has an answer nothing can retrieve.
        var store = chunks.Single(chunk => chunk.Symbol == "storeAccessToken");
        Assert.Contains("writeCookie(STORAGE_KEY", store.EmbedText, StringComparison.Ordinal);
        var explanation = chunks.Single(chunk =>
            chunk.EmbedText.Contains("out of localStorage on purpose", StringComparison.Ordinal));
        Assert.Equal(ChunkKind.Text, explanation.Kind);
    }

    [Fact]
    public async Task Puts_every_source_line_in_exactly_one_chunk()
    {
        var chunks = await ChunkAsync();

        foreach (var group in chunks.GroupBy(chunk => chunk.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            var lines = File.ReadAllLines(Path.Combine(_root, group.Key));
            var owners = new int[lines.Length + 2];
            foreach (var chunk in group)
            {
                for (var line = chunk.StartLine; line <= chunk.EndLine && line < owners.Length; line++)
                {
                    // A definition that contains others keeps a single line of each child as a
                    // table of contents, so the child's own lines have two claimants by design.
                    var nested = group.Any(other =>
                        other != chunk &&
                        other.StartLine <= line &&
                        other.EndLine >= line &&
                        other.EndLine - other.StartLine < chunk.EndLine - chunk.StartLine);
                    if (!nested)
                    {
                        owners[line]++;
                    }
                }
            }

            for (var line = 1; line <= lines.Length; line++)
            {
                if (string.IsNullOrWhiteSpace(lines[line - 1]))
                {
                    continue;
                }

                Assert.True(
                    owners[line] == 1,
                    $"{group.Key}:{line} is in {owners[line]} chunks: {lines[line - 1]}");
            }
        }
    }

    [Fact]
    public async Task Answers_every_question_the_committed_corpus_asks()
    {
        // The corpus is only worth committing if the repository still contains what it points at.
        // Both halves are checked: the file, by the evaluator's own validator, and the symbol, by
        // the chunker actually producing a chunk named that.
        var corpus = SearchEvaluationCorpus.Load(CorpusPath());
        Directory.CreateDirectory(_root);
        SyntheticFrontendRepository.Write(_root, Features);
        SearchEvaluationCorpus.ValidateAgainstSource(corpus, _root);

        var chunks = await ChunkAsync();
        var symbols = chunks
            .Select(chunk => chunk.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in corpus.Cases)
        {
            foreach (var target in item.Relevant)
            {
                if (string.IsNullOrWhiteSpace(target.Symbol) ||
                    target.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                {
                    // Python targets are checked by path only: `scip-python` is deliberately not
                    // installed on the runner, so its definitions are windowed there.
                    continue;
                }

                Assert.True(
                    symbols.Contains(target.Symbol),
                    $"Case '{item.Id}' names symbol '{target.Symbol}', which no chunk carries.");
            }
        }

        Assert.Equal(48, corpus.Cases.Count);
        Assert.Equal(4, corpus.Cases.Count(item => item.NoAnswer));
    }

    private async Task<List<Chunk>> ChunkAsync()
    {
        var executable = FixturePrerequisite.RequireText(
            ExecutableResolver.Find("scip-typescript"),
            "@sourcegraph/scip-typescript",
            "Install it with npm; the synthetic corpus is chunked with the real indexer.");

        Directory.CreateDirectory(_root);
        SyntheticFrontendRepository.Write(_root, Features);

        var result = await new ScipAdapterRunner().RunAsync(
            EmptyIndex(),
            _root,
            new ScipAdapterSpec(
                "typescript",
                executable,
                ["index", _root.Replace('\\', '/')],
                UnspecifiedPositionEncoding: ScipPositionEncoding.Utf16),
            TestContext.Current.CancellationToken);
        Assert.Equal(SemanticAdapterState.Succeeded, result.Status.State);

        var catalog = SymbolDefinitionCatalog.FromSemanticIndex(result.Index);
        var chunks = new List<Chunk>();
        foreach (var relative in FileScanner.Enumerate(_root))
        {
            var chunker = ChunkerFactory.Resolve(relative, catalog);
            if (chunker is null)
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(
                Path.Combine(_root, relative),
                TestContext.Current.CancellationToken);
            chunks.AddRange(chunker.Split(relative, text));
        }

        return chunks;
    }

    private static string CorpusPath()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(typeof(SyntheticCorpusChunkingTests).Assembly.Location)!);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            directory!.FullName,
            "tests",
            "CodeSearch.Tests",
            "Fixtures",
            "SearchEvaluation",
            "synthetic-frontend-cases.json");
    }

    private static SemanticIndex EmptyIndex() => new()
    {
        RepositoryId = "synthetic",
        GenerationId = "synthetic",
        GitTree = "synthetic",
        DirtyHash = null,
        BaseCommit = "synthetic",
        IndexedAtUtc = DateTime.UnixEpoch,
        Documents = [],
        Symbols = [],
        Occurrences = [],
        Relationships = [],
    };

    public void Dispose()
    {
        for (var attempt = 0; attempt < 50 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
