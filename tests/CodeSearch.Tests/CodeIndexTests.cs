using CodeSearch.Core.Chunking;
using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

public class CodeIndexTests
{
    [Fact]
    public void RoundTripsEveryFieldIncludingVectorsByteForByte()
    {
        const int dim = 7;
        var vectors = new float[3 * dim];
        for (var i = 0; i < vectors.Length; i++)
        {
            vectors[i] = i * 0.125f;
        }

        var original = new CodeIndex
        {
            Dim = dim,
            Model = "qwen3-embedding:4b",
            Root = @"R:\IntelWash",
            GitCommit = "bd3312eb6",
            IndexedAtUtc = new DateTime(2026, 7, 27, 8, 0, 0, DateTimeKind.Utc),
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = "dirty",
            Files =
            [
                new IndexedFile { RelPath = @"Src\A.cs", Hash = new byte[32], ChunkStart = 0, ChunkCount = 2 },
                new IndexedFile { RelPath = @"Src\Б.cs", Hash = Enumerable.Repeat((byte)7, 32).ToArray(), ChunkStart = 2, ChunkCount = 1 },
            ],
            Chunks =
            [
                Meta(0, ChunkKind.Type, "A", "public class A", "Ns", 1, 20),
                Meta(0, ChunkKind.Method, "A.Go", "public void Go()", "Ns", 5, 9, "Go hidden_token"),
                Meta(1, ChunkKind.Text, "Б.cs", "Б.cs:1-60", "Src", 1, 60),
            ],
            Vectors = vectors,
        };

        var path = Path.Combine(Path.GetTempPath(), $"codesearch-test-{Guid.NewGuid():N}.cidx");
        try
        {
            original.Save(path);
            var loaded = CodeIndex.Load(path);

            Assert.Equal(original.Dim, loaded.Dim);
            Assert.Equal(original.Model, loaded.Model);
            Assert.Equal(original.Root, loaded.Root);
            Assert.Equal(original.GitCommit, loaded.GitCommit);
            Assert.Equal(original.IndexedAtUtc, loaded.IndexedAtUtc);
            Assert.Equal(original.RepositoryId, loaded.RepositoryId);
            Assert.Equal(original.GenerationId, loaded.GenerationId);
            Assert.Equal(original.GitTree, loaded.GitTree);
            Assert.Equal(original.DirtyHash, loaded.DirtyHash);
            Assert.Equal(original.Vectors, loaded.Vectors);

            // Cyrillic paths survive - this codebase has them, and a codepage slip here would
            // silently orphan those files from every future incremental run.
            Assert.Equal(@"Src\Б.cs", loaded.Files[1].RelPath);
            Assert.Equal(original.Files[1].Hash, loaded.Files[1].Hash);
            Assert.Equal(
                original.Files[1].Hash,
                ((ISearchableIndex)loaded).FileHashAt(2).ToArray());

            Assert.Equal(original.Chunks.Count, loaded.Chunks.Count);
            Assert.Equal("A.Go", loaded.Chunks[1].Symbol);
            Assert.Equal("Go hidden_token", loaded.Chunks[1].LexicalText);
            Assert.Equal(ChunkKind.Text, loaded.Chunks[2].Kind);
            Assert.Equal(60, loaded.Chunks[2].EndLine);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HeaderOnlyLoadSkipsVectors()
    {
        var index = new CodeIndex
        {
            Dim = 4,
            Model = "m",
            Root = "r",
            GitCommit = "c",
            IndexedAtUtc = DateTime.UtcNow,
            Files = [new IndexedFile { RelPath = "A.cs", Hash = new byte[32], ChunkStart = 0, ChunkCount = 1 }],
            Chunks = [Meta(0, ChunkKind.Type, "A", "class A", "", 1, 2)],
            Vectors = [1, 0, 0, 0],
        };

        var path = Path.Combine(Path.GetTempPath(), $"codesearch-test-{Guid.NewGuid():N}.cidx");
        try
        {
            index.Save(path);
            var loaded = CodeIndex.Load(path, withVectors: false);

            Assert.Empty(loaded.Vectors);
            Assert.Single(loaded.Chunks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsAFileThatIsNotAnIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codesearch-test-{Guid.NewGuid():N}.cidx");
        try
        {
            File.WriteAllText(path, "definitely not an index");
            Assert.Throws<InvalidDataException>(() => CodeIndex.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ChunkMeta Meta(
        int file,
        ChunkKind kind,
        string symbol,
        string signature,
        string ns,
        int start,
        int end,
        string lexicalText = "") =>
        new()
        {
            FileIndex = file,
            Kind = kind,
            Symbol = symbol,
            Signature = signature,
            Namespace = ns,
            LexicalText = lexicalText,
            StartLine = start,
            EndLine = end,
        };
}
