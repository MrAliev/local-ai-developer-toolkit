using System.Diagnostics;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// A search hit is one snapshot or a named refusal (#197). Metadata, rank and chunk_id
/// come from the index snapshot; a snippet read from a file that no longer matches it
/// used to dress that snapshot in another state's text — and get_code_chunk then
/// correctly fail-closed on the same identity, so one workflow contradicted itself.
/// </summary>
public sealed class SearchSnapshotDriftTests : IDisposable
{
    private const string CalibratedModel = "qwen3-embedding:8b-q8_0";
    private const string AlphaContent = "first line\r\nsecond line\r\nthird line\r\n";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-drift-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "codesearch-drift-runtime-" + Guid.NewGuid().ToString("N"));

    private readonly WorkingIndexIdentity _identity;

    public SearchSnapshotDriftTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), AlphaContent);
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@local.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("add", ".");
        Git("commit", "-m", "Initial");
        _identity = RuntimeIndexLayout.Inspect(_root, _runtimeRoot);
    }

    [Fact]
    public async Task A_file_matching_its_recorded_hash_serves_the_snippet()
    {
        PublishWithHash(CanonicalIndexText.Hash(AlphaContent));

        var hit = Assert.Single(await Search());

        Assert.Equal("first line\n    ...", hit.Snippet);
    }

    [Fact]
    public async Task A_file_that_no_longer_matches_the_snapshot_carries_the_named_marker()
    {
        // The recorded hash disagrees with the file on disk: exactly what a worktree edit
        // between indexing and this search produces. The metadata stays — it honestly
        // describes the snapshot — and the snippet is the named refusal, never mixed text.
        PublishWithHash(CanonicalIndexText.Hash("what the index believed\r\n"));

        var hit = Assert.Single(await Search());

        Assert.Equal(SearchEngine.SnapshotChangedSnippet, hit.Snippet);
        Assert.Equal("Alpha.cs", hit.RelPath);
        Assert.Equal("Example.ExactSymbol", hit.Symbol);
        Assert.NotEmpty(hit.ChunkId);
    }

    [Fact]
    public async Task An_unrecorded_hash_keeps_the_old_read_what_is_there_behaviour()
    {
        PublishWithHash(new byte[32]);

        var hit = Assert.Single(await Search());

        Assert.Equal("first line\n    ...", hit.Snippet);
    }

    private async Task<IReadOnlyList<SearchHit>> Search()
    {
        var service = new SearchService(
            model => new ThrowingClient(
                model,
                new EmbeddingUnavailableException(
                    "broker unavailable",
                    new TimeoutException())),
            runtimeRoot: _runtimeRoot);
        return await service.SearchAsync(
            "ExactSymbol",
            _root,
            new SearchOptions
            {
                TopK = 1,
                MaxPerFile = 1,
                SnippetLines = 1,
            },
            TestContext.Current.CancellationToken);
    }

    private void PublishWithHash(byte[] alphaHash)
    {
        var generation = new GenerationIdentity(
            _identity.RepositoryId,
            _identity.HeadCommit,
            _identity.HeadTree,
            CalibratedModel,
            2,
            2,
            CodeIndex.CurrentVersion,
            2,
            2);
        var index = new CodeIndex
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
                    Hash = alphaHash,
                    ChunkStart = 0,
                    ChunkCount = 1,
                },
            ],
            Chunks =
            [
                new ChunkMeta
                {
                    FileIndex = 0,
                    Kind = ChunkKind.Method,
                    Symbol = "Example.ExactSymbol",
                    Signature = "void ExactSymbol()",
                    Namespace = "Example",
                    StartLine = 1,
                    EndLine = 3,
                },
            ],
            Vectors = [1f, 0f],
        };
        var sourceIndex = Path.Combine(
            Path.GetTempPath(),
            generation.Id + "-" + Guid.NewGuid().ToString("N") + ".cidx");
        try
        {
            index.Save(sourceIndex);
            var store = new GenerationStore(_identity.RepositoryRuntimeRoot.Value);
            store.SetCurrent(store.PublishIndex(sourceIndex, generation));
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
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class ThrowingClient(
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

    public void Dispose()
    {
        foreach (var path in new[] { _runtimeRoot, _root })
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(
                                 path,
                                 "*",
                                 SearchOption.AllDirectories))
                    {
                        File.SetAttributes(entry, FileAttributes.Normal);
                    }

                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
