using System.Diagnostics;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using CodeSearch.Mcp;

namespace CodeSearch.Tests;

public sealed class SearchChunkServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-chunk-" + Guid.NewGuid().ToString("N"));
    private readonly WorkingIndexIdentity _identity;
    private readonly GenerationIdentity _generation;

    public SearchChunkServiceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "Example.cs"),
            "line one\r\nline two\r\nline three\r\nline four\r\nline five\r\n");
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@local.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("add", "Example.cs");
        Git("commit", "-m", "Initial");

        _identity = RuntimeIndexLayout.Inspect(_root);
        _generation = new GenerationIdentity(
            _identity.RepositoryId,
            _identity.HeadCommit,
            _identity.HeadTree,
            "test-model",
            2,
            1,
            CodeIndex.CurrentVersion,
            1,
            1);

        var sourceIndex = Path.Combine(
            Path.GetTempPath(),
            _generation.Id + "-" + Guid.NewGuid().ToString("N") + ".cidx");
        try
        {
            new CodeIndex
            {
                Dim = 2,
                Model = "test-model",
                Root = _root,
                GitCommit = _identity.HeadCommit,
                GitTree = _identity.HeadTree,
                RepositoryId = _identity.RepositoryId,
                GenerationId = _generation.Id,
                IndexedAtUtc = DateTime.UtcNow,
                Files =
                [
                    new IndexedFile
                    {
                        RelPath = "Example.cs",
                        Hash = new byte[32],
                        ChunkStart = 0,
                        ChunkCount = 1
                    }
                ],
                Chunks =
                [
                    new ChunkMeta
                    {
                        FileIndex = 0,
                        Kind = ChunkKind.Method,
                        Symbol = "Example.Run",
                        Signature = "void Run()",
                        Namespace = "Example",
                        StartLine = 2,
                        EndLine = 4
                    }
                ],
                Vectors = [1f, 0f]
            }.Save(sourceIndex);

            var store = new GenerationStore(_identity.RepositoryRuntimeRoot);
            var manifest = store.PublishIndex(sourceIndex, _generation);
            store.SetCurrent(manifest);
        }
        finally
        {
            File.Delete(sourceIndex);
        }
    }

    [Fact]
    public async Task Resolves_the_full_chunk_body_from_the_exact_snapshot()
    {
        var id = new SearchChunkId(
            _identity.RepositoryId,
            _generation.Id,
            _identity.HeadTree,
            null,
            0).Encode();

        var chunk = await new SearchService().GetChunkAsync(
            id,
            _root,
            TestContext.Current.CancellationToken);

        Assert.Equal(id, chunk.ChunkId);
        Assert.Equal("Example.cs", chunk.RelPath);
        Assert.Equal(2, chunk.StartLine);
        Assert.Equal(4, chunk.EndLine);
        Assert.Equal("line two\nline three\nline four", chunk.Body);
        Assert.Equal("Example.Run", chunk.Symbol);
    }

    [Fact]
    public async Task Refuses_a_chunk_id_after_the_worktree_becomes_dirty()
    {
        var id = new SearchChunkId(
            _identity.RepositoryId,
            _generation.Id,
            _identity.HeadTree,
            null,
            0).Encode();
        File.AppendAllText(Path.Combine(_root, "Example.cs"), "// changed\r\n");

        var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
            () => new SearchService().GetChunkAsync(
                id,
                _root,
                TestContext.Current.CancellationToken));

        Assert.Equal("stale_overlay", error.Code);
    }

    [Fact]
    public async Task Mcp_tool_returns_full_source_and_metadata()
    {
        var id = new SearchChunkId(
            _identity.RepositoryId,
            _generation.Id,
            _identity.HeadTree,
            null,
            0).Encode();

        var response = await CodeSearchTools.GetCodeChunk(
            new SearchService(),
            id,
            _root,
            TestContext.Current.CancellationToken);

        Assert.Contains("Example.cs:2-4", response, StringComparison.Ordinal);
        Assert.Contains("Example.Run", response, StringComparison.Ordinal);
        Assert.Contains("line two\nline three\nline four", response, StringComparison.Ordinal);
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
        DeleteTree(_identity.RepositoryRuntimeRoot);
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
}
