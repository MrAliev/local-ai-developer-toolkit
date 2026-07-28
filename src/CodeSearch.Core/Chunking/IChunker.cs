namespace CodeSearch.Core.Chunking;

public interface IChunker
{
    IEnumerable<Chunk> Split(string relPath, string content);
}

public static class ChunkLimits
{
    /// <summary>
    /// Max characters of embed text per chunk. Roughly 1500 tokens — comfortably inside any
    /// embedding model's window, and small enough that a chunk stays about one idea.
    /// </summary>
    public const int MaxChars = 6000;

    /// <summary>Lines of overlap when a member has to be split across several chunks.</summary>
    public const int SplitOverlapLines = 8;

    /// <summary>Files bigger than this are skipped outright — generated or vendored, never worth embedding.</summary>
    public const int MaxFileBytes = 400_000;
}
