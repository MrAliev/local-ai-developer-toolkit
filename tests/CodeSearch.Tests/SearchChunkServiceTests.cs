using System.Diagnostics;
using System.Text;
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

        PublishIndex(_identity, _generation, "Example.cs", 2, 4);
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
        var id = CurrentId().Encode();
        File.AppendAllText(Path.Combine(_root, "Example.cs"), "// changed\r\n");

        var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
            () => new SearchService().GetChunkAsync(
                id,
                _root,
                TestContext.Current.CancellationToken));

        Assert.Equal("stale_overlay", error.Code);
    }

    [Theory]
    [InlineData("repository", "wrong_repository")]
    [InlineData("generation", "stale_generation")]
    [InlineData("tree", "stale_worktree")]
    public async Task Refuses_chunk_ids_for_a_different_snapshot(
        string changedField,
        string expectedCode)
    {
        var id = changedField switch
        {
            "repository" => CurrentId() with { RepositoryId = "other" },
            "generation" => CurrentId() with { GenerationId = "other" },
            "tree" => CurrentId() with { GitTree = "other" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };

        var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
            () => new SearchService().GetChunkAsync(
                id.Encode(),
                _root,
                TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task Refuses_a_chunk_ordinal_outside_the_exact_snapshot()
    {
        var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
            () => new SearchService().GetChunkAsync(
                (CurrentId() with { Ordinal = 1 }).Encode(),
                _root,
                TestContext.Current.CancellationToken));

        Assert.Equal("chunk_out_of_range", error.Code);
    }

    [Fact]
    public async Task Refuses_an_indexed_range_that_no_longer_exists()
    {
        var store = new GenerationStore(_identity.RepositoryRuntimeRoot);
        CreateIndex(_identity, _generation, "Example.cs", 2, 99)
            .Save(store.IndexPath(_generation.Id));

        var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
            () => new SearchService().GetChunkAsync(
                CurrentId().Encode(),
                _root,
                TestContext.Current.CancellationToken));

        Assert.Equal("stale_source_range", error.Code);
    }

    [Fact]
    public async Task Refuses_an_indexed_path_outside_the_repository()
    {
        var store = new GenerationStore(_identity.RepositoryRuntimeRoot);
        CreateIndex(
                _identity,
                _generation,
                Path.Combine("..", "Outside.cs"),
                1,
                1)
            .Save(store.IndexPath(_generation.Id));

        var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
            () => new SearchService().GetChunkAsync(
                CurrentId().Encode(),
                _root,
                TestContext.Current.CancellationToken));

        Assert.Equal("unsafe_chunk_path", error.Code);
    }

    [Fact]
    public async Task Refuses_a_missing_indexed_source_with_an_explicit_diagnostic()
    {
        var store = new GenerationStore(_identity.RepositoryRuntimeRoot);
        CreateIndex(_identity, _generation, "Missing.cs", 1, 1)
            .Save(store.IndexPath(_generation.Id));

        var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
            () => new SearchService().GetChunkAsync(
                CurrentId().Encode(),
                _root,
                TestContext.Current.CancellationToken));

        Assert.Equal("chunk_source_missing", error.Code);
    }

    [Fact]
    public async Task Refuses_a_reparse_point_directory_before_reading_outside_the_repository()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "codesearch-outside-" + Guid.NewGuid().ToString("N"));
        var outsidePath = Path.Combine(outsideRoot, "Outside.cs");
        var linkPath = Path.Combine(_root, "Linked");
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(outsidePath, "outside one\r\noutside two\r\n");
        try
        {
            CreateDirectoryLink(linkPath, outsideRoot);
            var identity = RuntimeIndexLayout.Inspect(_root);
            Assert.NotNull(identity.DirtyHash);
            var overlayPath = RuntimeIndexLayout.OverlayPath(
                identity,
                _generation.Id);
            CreateIndex(
                    identity,
                    _generation,
                    Path.Combine("Linked", "Outside.cs"),
                    1,
                    1,
                    _identity.HeadCommit)
                .Save(overlayPath);
            var id = new SearchChunkId(
                identity.RepositoryId,
                _generation.Id,
                identity.HeadTree,
                identity.DirtyHash,
                0).Encode();

            var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
                () => new SearchService().GetChunkAsync(
                    id,
                    _root,
                    TestContext.Current.CancellationToken));

            Assert.Equal("unsafe_chunk_reparse_point", error.Code);
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
    public async Task Refuses_a_symbolic_linked_source_before_reading_outside_the_repository()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "codesearch-outside-" + Guid.NewGuid().ToString("N"));
        var outsidePath = Path.Combine(outsideRoot, "Outside.cs");
        var linkPath = Path.Combine(_root, "Linked.cs");
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(outsidePath, "outside one\r\noutside two\r\n");
        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, outsidePath);
            }
            catch (Exception ex) when (
                OperatingSystem.IsWindows() &&
                ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("Creating symbolic links requires Windows Developer Mode.");
                return;
            }

            Git("config", "core.symlinks", "true");
            Git("add", "Linked.cs");
            Git("commit", "-m", "Add linked source");
            var identity = RuntimeIndexLayout.Inspect(_root);
            var generation = new GenerationIdentity(
                identity.RepositoryId,
                identity.HeadCommit,
                identity.HeadTree,
                "test-model",
                2,
                1,
                CodeIndex.CurrentVersion,
                1,
                1);
            PublishIndex(identity, generation, "Linked.cs", 1, 1);
            var id = new SearchChunkId(
                identity.RepositoryId,
                generation.Id,
                identity.HeadTree,
                null,
                0).Encode();

            var error = await Assert.ThrowsAsync<SearchChunkResolutionException>(
                () => new SearchService().GetChunkAsync(
                    id,
                    _root,
                    TestContext.Current.CancellationToken));

            Assert.Equal("unsafe_chunk_reparse_point", error.Code);
        }
        finally
        {
            File.Delete(linkPath);
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Legacy_indexes_fail_with_rebuild_guidance_before_requesting_an_embedding()
    {
        DeleteTree(_identity.RepositoryRuntimeRoot);
        var legacyPath = RepoLocator.LegacyIndexPathFor(_root);
        WriteLegacyIndexV2(legacyPath);
        var embeddingRequested = false;
        var service = new SearchService(_ =>
        {
            embeddingRequested = true;
            throw new InvalidOperationException("Embedding must not be requested.");
        });
        try
        {
            var error = await Assert.ThrowsAsync<SearchNotReadyException>(
                () => service.SearchAsync(
                    "example",
                    _root,
                    new SearchOptions
                    {
                        AllowUncalibratedModelForEvaluation = true
                    },
                    TestContext.Current.CancellationToken));

            Assert.False(embeddingRequested);
            Assert.Contains(
                "rebuild",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(legacyPath);
        }
    }

    [Fact]
    public async Task Mcp_tool_returns_full_source_and_metadata()
    {
        var id = CurrentId().Encode();

        var response = await CodeSearchTools.GetCodeChunk(
            new SearchService(),
            id,
            _root,
            TestContext.Current.CancellationToken);

        Assert.Contains("Example.cs:2-4", response, StringComparison.Ordinal);
        Assert.Contains("Example.Run", response, StringComparison.Ordinal);
        Assert.Contains("line two\nline three\nline four", response, StringComparison.Ordinal);
    }

    private SearchChunkId CurrentId() =>
        new(
            _identity.RepositoryId,
            _generation.Id,
            _identity.HeadTree,
            null,
            0);

    private void PublishIndex(
        WorkingIndexIdentity identity,
        GenerationIdentity generation,
        string relPath,
        int startLine,
        int endLine)
    {
        var sourceIndex = Path.Combine(
            Path.GetTempPath(),
            generation.Id + "-" + Guid.NewGuid().ToString("N") + ".cidx");
        try
        {
            CreateIndex(identity, generation, relPath, startLine, endLine)
                .Save(sourceIndex);
            var store = new GenerationStore(identity.RepositoryRuntimeRoot);
            var manifest = store.PublishIndex(sourceIndex, generation);
            store.SetCurrent(manifest);
        }
        finally
        {
            File.Delete(sourceIndex);
        }
    }

    private CodeIndex CreateIndex(
        WorkingIndexIdentity identity,
        GenerationIdentity generation,
        string relPath,
        int startLine,
        int endLine,
        string? baseCommit = null) =>
        new()
        {
            Dim = 2,
            Model = "test-model",
            Root = _root,
            GitCommit = identity.HeadCommit,
            GitTree = identity.HeadTree,
            RepositoryId = identity.RepositoryId,
            GenerationId = generation.Id,
            DirtyHash = identity.DirtyHash,
            BaseCommit = baseCommit ?? string.Empty,
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
                new ChunkMeta
                {
                    FileIndex = 0,
                    Kind = ChunkKind.Method,
                    Symbol = "Example.Run",
                    Signature = "void Run()",
                    Namespace = "Example",
                    StartLine = startLine,
                    EndLine = endLine
                }
            ],
            Vectors = [1f, 0f]
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

    private void WriteLegacyIndexV2(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Encoding.ASCII.GetBytes("CIDX"));
        writer.Write(2);
        writer.Write(2);
        writer.Write("test-model");
        writer.Write(_root);
        writer.Write(_identity.HeadCommit);
        writer.Write(DateTime.UtcNow.Ticks);
        writer.Write(string.Empty);
        writer.Write(0);
        writer.Write(1);
        writer.Write("Example.cs");
        writer.Write(new byte[32]);
        writer.Write(0);
        writer.Write(1);
        writer.Write(1);
        writer.Write(0);
        writer.Write((byte)ChunkKind.Method);
        writer.Write("Example.Run");
        writer.Write("void Run()");
        writer.Write("Example");
        writer.Write(2);
        writer.Write(4);
        writer.Write(1f);
        writer.Write(0f);
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
