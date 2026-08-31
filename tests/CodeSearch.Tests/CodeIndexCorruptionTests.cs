using CodeSearch.Core.Chunking;
using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

/// <summary>
/// The durable binary cache must not trust anything the file says about itself (#205):
/// disk corruption used to become an OOM or a random runtime exception instead of the one
/// message that helps — the index is corrupt, rebuild it.
/// </summary>
public sealed class CodeIndexCorruptionTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"codesearch-corrupt-{Guid.NewGuid():N}.cidx");

    [Fact]
    public void A_truncated_index_names_corruption_not_the_stream()
    {
        BuildIndex().Save(_path);
        var bytes = File.ReadAllBytes(_path);
        File.WriteAllBytes(_path, bytes[..^40]);

        var error = Assert.Throws<InvalidDataException>(() => CodeIndex.Load(_path));

        Assert.Contains("rebuild", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trailing_bytes_after_the_vector_block_are_refused()
    {
        BuildIndex().Save(_path);
        using (var stream = new FileStream(_path, FileMode.Append))
        {
            stream.Write("garbage"u8);
        }

        var error = Assert.Throws<InvalidDataException>(() => CodeIndex.Load(_path));

        Assert.Contains("trailing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_non_finite_vector_is_corruption_not_a_ranking_surprise()
    {
        var index = BuildIndex();
        index.Vectors[2] = float.NaN;
        index.Save(_path);

        var error = Assert.Throws<InvalidDataException>(() => CodeIndex.Load(_path));

        Assert.Contains("non-finite", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_chunk_pointing_at_a_missing_file_is_refused()
    {
        BuildIndex(fileIndex: 7).Save(_path);

        var error = Assert.Throws<InvalidDataException>(() => CodeIndex.Load(_path));

        Assert.Contains("file index", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_impossible_line_range_is_refused()
    {
        BuildIndex(startLine: 0).Save(_path);

        var error = Assert.Throws<InvalidDataException>(() => CodeIndex.Load(_path));

        Assert.Contains("line range", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_whose_chunk_range_overruns_the_chunks_is_refused()
    {
        BuildIndex(fileChunkCount: 40).Save(_path);

        var error = Assert.Throws<InvalidDataException>(() => CodeIndex.Load(_path));

        Assert.Contains("chunk range", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_healthy_index_still_round_trips()
    {
        BuildIndex().Save(_path);

        var loaded = CodeIndex.Load(_path);

        Assert.Equal(4, loaded.Dim);
        Assert.Single(loaded.Files);
        Assert.Single(loaded.Chunks);
        Assert.Equal(4, loaded.Vectors.Length);
    }

    private static CodeIndex BuildIndex(
        int fileIndex = 0,
        int startLine = 1,
        int fileChunkCount = 1) => new()
    {
        Dim = 4,
        Model = "test-model",
        Root = Path.GetTempPath(),
        GitCommit = "abc123",
        IndexedAtUtc = DateTime.UtcNow,
        Files =
        [
            new IndexedFile
            {
                RelPath = "A.cs",
                Hash = new byte[32],
                ChunkStart = 0,
                ChunkCount = fileChunkCount,
            },
        ],
        Chunks =
        [
            new ChunkMeta
            {
                FileIndex = fileIndex,
                Kind = ChunkKind.Type,
                Symbol = "A",
                Signature = "public class A",
                Namespace = "Ns",
                StartLine = startLine,
                EndLine = 5,
            },
        ],
        Vectors = [1, 0, 0, 0],
    };

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
