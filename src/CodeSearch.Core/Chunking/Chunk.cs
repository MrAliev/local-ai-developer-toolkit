namespace CodeSearch.Core.Chunking;

/// <summary>
/// What a chunk represents. Stored as a single byte in the index, so values are pinned.
/// </summary>
public enum ChunkKind : byte
{
    /// <summary>Whole-file summary. Only emitted for source files that declare no types.</summary>
    File = 0,

    /// <summary>A type declaration with its fields/properties and a table of contents of its methods.</summary>
    Type = 1,

    /// <summary>A single executable member: method, constructor, operator, indexer, or a non-trivial property.</summary>
    Method = 2,

    /// <summary>A sliding line window from a non-C# text file.</summary>
    Text = 3,
}

/// <summary>
/// One unit of retrieval. <see cref="EmbedText"/> is what gets embedded, and is deliberately
/// NOT persisted in the index — snippets are re-read from disk at query time, which keeps the
/// index to vectors plus metadata and means displayed code is never stale relative to the file.
/// </summary>
public sealed record Chunk
{
    public required string RelPath { get; init; }
    public required ChunkKind Kind { get; init; }

    /// <summary>Dotted symbol path within the file, e.g. <c>OrderService.CloseOrder</c>.</summary>
    public required string Symbol { get; init; }

    /// <summary>Single-line declaration, used both for display and for lexical matching.</summary>
    public required string Signature { get; init; }

    public required string Namespace { get; init; }

    /// <summary>1-based, inclusive.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based, inclusive.</summary>
    public required int EndLine { get; init; }

    public required string EmbedText { get; init; }
}
