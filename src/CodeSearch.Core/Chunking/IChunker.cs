namespace CodeSearch.Core.Chunking;

public interface IChunker
{
    IEnumerable<Chunk> Split(string relPath, string content);
}

public static class ChunkLimits
{
    /// <summary>
    /// Max characters of embed text per chunk, small enough that a chunk stays about one idea.
    ///
    /// Do not read this as a token count. Dense C# measures about 1.5 characters per token —
    /// a 6136-character chunk tokenized to 4095 — so a full-size chunk approaches 4000 tokens,
    /// not the 1500 an English-prose ratio would suggest. <see cref="EmbeddingContextTokens"/>
    /// is derived from this number and the two must move together.
    /// </summary>
    public const int MaxChars = 6000;

    /// <summary>
    /// Context tier requested for every embedding call. Worst case a chunk tokenizes at one
    /// token per character, so <see cref="MaxChars"/> plus the chunk header cannot exceed this
    /// tier. It is deliberately constant: Ollama reloads the runner when num_ctx changes, so
    /// sizing per request would trade one HTTP 400 for a model reload every few batches.
    /// </summary>
    public const int EmbeddingContextTokens = 8192;

    /// <summary>Lines of overlap when a member has to be split across several chunks.</summary>
    public const int SplitOverlapLines = 8;

    /// <summary>Files bigger than this are skipped outright — generated or vendored, never worth embedding.</summary>
    public const int MaxFileBytes = 400_000;
}
