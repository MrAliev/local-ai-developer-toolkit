using System.Runtime.InteropServices;
using System.Text;
using CodeSearch.Core.Chunking;

namespace CodeSearch.Core.Indexing;

public sealed class IndexedFile
{
    public required string RelPath { get; init; }

    /// <summary>SHA-256 of the file's text. Drives incremental rebuilds.</summary>
    public required byte[] Hash { get; init; }

    public required int ChunkStart { get; init; }

    public required int ChunkCount { get; init; }
}

public sealed class ChunkMeta
{
    public required int FileIndex { get; init; }
    public required ChunkKind Kind { get; init; }
    public required string Symbol { get; init; }
    public required string Signature { get; init; }
    public required string Namespace { get; init; }
    public string LexicalText { get; init; } = string.Empty;
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
}

/// <summary>
/// The on-disk index. Custom binary rather than a vector database on purpose: at tens of
/// thousands of chunks a brute-force SIMD dot product is single-digit milliseconds, so an ANN
/// index would add a dependency and an approximation in exchange for nothing.
///
/// Layout (all strings are BinaryWriter length-prefixed UTF-8):
/// <code>
///   "CIDX" | int version | int dim | string model | string root | string gitCommit
///   long indexedAtUtcTicks
///   int fileCount   -> per file: string relPath, byte[32] hash, int chunkStart, int chunkCount
///   int chunkCount  -> per chunk: int fileIndex, byte kind, string symbol, string signature,
///                                 string ns, string lexicalText, int startLine, int endLine
///   float32[chunkCount * dim]  (contiguous, L2-normalized)
/// </code>
/// Vectors live in one contiguous trailing block so they can be read and written in bulk slabs.
/// </summary>
public sealed class CodeIndex : ISearchableIndex
{
    /// <summary>
    /// v2 adds the overlay fields. v3 adds exact repository/generation/tree/dirty identities.
    /// v4 persists lexical chunk text so exact identifiers work in every indexed language.
    /// Older files remain readable for diagnostics. Moving to v4 intentionally performs one
    /// full rebuild because older containers do not retain the source text needed to populate
    /// the language-independent lexical field.
    /// </summary>
    public const int CurrentVersion = 4;

    private const string Magic = "CIDX";

    /// <summary>
    /// Bulk-copy slab size. Reading or writing vectors element-by-element was the single worst
    /// performance bug in the PowerShell predecessor (90s per query); this keeps it to a handful
    /// of large Buffer.BlockCopy calls instead.
    /// </summary>
    private const int SlabBytes = 8 * 1024 * 1024;

    public required int Dim { get; init; }
    public required string Model { get; init; }
    public required string Root { get; init; }
    public required string GitCommit { get; init; }
    public required DateTime IndexedAtUtc { get; init; }
    public required List<IndexedFile> Files { get; init; }
    public required List<ChunkMeta> Chunks { get; init; }

    public int FormatVersion { get; init; } = CurrentVersion;

    public string RepositoryId { get; init; } = string.Empty;

    public string GenerationId { get; init; } = string.Empty;

    public string GitTree { get; init; } = string.Empty;

    public string? DirtyHash { get; init; }

    /// <summary>
    /// Empty for a base index. For an overlay, the commit of the base it was computed against -
    /// searching an overlay on top of a different base would mix vectors of files that have since
    /// changed underneath it.
    /// </summary>
    public string BaseCommit { get; init; } = string.Empty;

    /// <summary>
    /// Overlay only: paths the base index has but this working tree does not. Without them a
    /// file deleted on a branch would keep answering searches from the base forever.
    /// </summary>
    public List<string> DeletedPaths { get; init; } = [];

    public bool IsOverlay => BaseCommit.Length > 0 || DeletedPaths.Count > 0;

    /// <summary>Chunks.Count * Dim floats, or empty when loaded with <c>withVectors: false</c>.</summary>
    public required float[] Vectors { get; init; }

    public ReadOnlySpan<float> VectorAt(int chunkIndex) =>
        Vectors.AsSpan(chunkIndex * Dim, Dim);

    int ISearchableIndex.ChunkCount => Chunks.Count;

    ChunkMeta ISearchableIndex.ChunkAt(int index) => Chunks[index];

    string ISearchableIndex.PathOf(int index) => Files[Chunks[index].FileIndex].RelPath;

    ReadOnlySpan<byte> ISearchableIndex.FileHashAt(int index) =>
        Files[Chunks[index].FileIndex].Hash;

    /// <summary>
    /// Loads a durable binary cache without trusting anything the file says about itself
    /// (#205). Counts and string lengths used to size allocations and loops unchecked, so a
    /// corrupt or truncated index answered with an OOM or a random runtime exception instead
    /// of the one message that helps: the index is corrupt, rebuild it. Every count is now
    /// bounded by the bytes actually remaining, every string length is validated before its
    /// allocation, hashes must be exactly 32 bytes, cross-references and enums must resolve,
    /// vectors must be finite, and a full read must end at the end of the file.
    /// </summary>
    public static CodeIndex Load(string path, bool withVectors = true)
    {
        try
        {
            return LoadValidated(path, withVectors);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException(
                $"Index '{path}' ended mid-record. The index is corrupt; rebuild it with " +
                "`localai sync`.",
                exception);
        }
    }

    private static CodeIndex LoadValidated(string path, bool withVectors)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != Magic)
        {
            throw new InvalidDataException($"'{path}' is not a CodeSearch index (magic '{magic}').");
        }

        var version = reader.ReadInt32();
        if (version is < 1 or > CurrentVersion)
        {
            throw new InvalidDataException(
                $"Index '{path}' is version {version}, this build reads up to {CurrentVersion}. Rebuild it.");
        }

        var dim = reader.ReadInt32();
        if (dim is < 1 or > 100_000)
        {
            throw Corrupt(path, $"embedding dimension {dim}");
        }

        var model = ReadBoundedString(reader, stream, path);
        var root = ReadBoundedString(reader, stream, path);
        var commit = ReadBoundedString(reader, stream, path);
        var indexedAt = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);

        var baseCommit = string.Empty;
        var deleted = new List<string>();
        if (version >= 2)
        {
            baseCommit = ReadBoundedString(reader, stream, path);
            var deletedCount = ReadBoundedCount(reader, stream, path, minimumEntryBytes: 1);
            deleted.Capacity = deletedCount;
            for (var i = 0; i < deletedCount; i++)
            {
                deleted.Add(ReadBoundedString(reader, stream, path));
            }
        }

        var repositoryId = string.Empty;
        var generationId = string.Empty;
        var gitTree = string.Empty;
        string? dirtyHash = null;
        if (version >= 3)
        {
            repositoryId = ReadBoundedString(reader, stream, path);
            generationId = ReadBoundedString(reader, stream, path);
            gitTree = ReadBoundedString(reader, stream, path);
            dirtyHash = reader.ReadBoolean()
                ? ReadBoundedString(reader, stream, path)
                : null;
        }

        var fileCount = ReadBoundedCount(reader, stream, path, minimumEntryBytes: 41);
        var files = new List<IndexedFile>(fileCount);
        for (var i = 0; i < fileCount; i++)
        {
            var relPath = ReadBoundedString(reader, stream, path);
            var hash = reader.ReadBytes(32);
            if (hash.Length != 32)
            {
                throw Corrupt(path, $"a {hash.Length}-byte file hash");
            }

            files.Add(new IndexedFile
            {
                RelPath = relPath,
                Hash = hash,
                ChunkStart = reader.ReadInt32(),
                ChunkCount = reader.ReadInt32(),
            });
        }

        var chunkCount = ReadBoundedCount(reader, stream, path, minimumEntryBytes: 12);
        var chunks = new List<ChunkMeta>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            var fileIndex = reader.ReadInt32();
            if (fileIndex < 0 || fileIndex >= files.Count)
            {
                throw Corrupt(path, $"chunk file index {fileIndex} of {files.Count}");
            }

            var kind = (ChunkKind)reader.ReadByte();
            if (!Enum.IsDefined(kind))
            {
                throw Corrupt(path, $"chunk kind {(byte)kind}");
            }

            var symbol = ReadBoundedString(reader, stream, path);
            var signature = ReadBoundedString(reader, stream, path);
            var chunkNamespace = ReadBoundedString(reader, stream, path);
            var lexicalText = version >= 4
                ? ReadBoundedString(reader, stream, path)
                : string.Empty;
            var startLine = reader.ReadInt32();
            var endLine = reader.ReadInt32();
            if (startLine < 1 || endLine < startLine)
            {
                throw Corrupt(path, $"chunk line range {startLine}..{endLine}");
            }

            chunks.Add(new ChunkMeta
            {
                FileIndex = fileIndex,
                Kind = kind,
                Symbol = symbol,
                Signature = signature,
                Namespace = chunkNamespace,
                LexicalText = lexicalText,
                StartLine = startLine,
                EndLine = endLine,
            });
        }

        foreach (var file in files)
        {
            if (file.ChunkStart < 0 ||
                file.ChunkCount < 0 ||
                (long)file.ChunkStart + file.ChunkCount > chunks.Count)
            {
                throw Corrupt(
                    path,
                    $"file chunk range {file.ChunkStart}+{file.ChunkCount} of {chunks.Count}");
            }
        }

        var vectors = withVectors ? ReadVectors(reader, chunkCount, dim, path) : [];
        if (withVectors && stream.Position != stream.Length)
        {
            throw Corrupt(
                path,
                $"{stream.Length - stream.Position} trailing bytes after the vector block");
        }

        return new CodeIndex
        {
            Dim = dim,
            Model = model,
            Root = root,
            GitCommit = commit,
            IndexedAtUtc = indexedAt,
            Files = files,
            Chunks = chunks,
            Vectors = vectors,
            BaseCommit = baseCommit,
            DeletedPaths = deleted,
            RepositoryId = repositoryId,
            GenerationId = generationId,
            GitTree = gitTree,
            DirtyHash = dirtyHash,
            FormatVersion = version,
        };
    }

    private static InvalidDataException Corrupt(string path, string what) =>
        new($"Index '{path}' declares {what}. The index is corrupt; rebuild it with `localai sync`.");

    /// <summary>
    /// Wire-compatible with BinaryReader.ReadString — the same 7-bit length prefix — but the
    /// declared length is validated against the bytes actually remaining before anything is
    /// allocated, so a corrupt prefix cannot ask for gigabytes.
    /// </summary>
    private static string ReadBoundedString(BinaryReader reader, Stream stream, string path)
    {
        var length = reader.Read7BitEncodedInt();
        if (length < 0 || length > stream.Length - stream.Position)
        {
            throw Corrupt(path, $"a {length}-byte string with {stream.Length - stream.Position} bytes left");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw Corrupt(path, "a string ending mid-record");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static int ReadBoundedCount(
        BinaryReader reader,
        Stream stream,
        string path,
        long minimumEntryBytes)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > (stream.Length - stream.Position) / minimumEntryBytes)
        {
            throw Corrupt(path, $"{count} entries with {stream.Length - stream.Position} bytes left");
        }

        return count;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file and move into place, so a crash mid-write leaves the
        // previous index intact rather than a truncated one.
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Encoding.ASCII.GetBytes(Magic));
            writer.Write(CurrentVersion);
            writer.Write(Dim);
            writer.Write(Model);
            writer.Write(Root);
            writer.Write(GitCommit);
            writer.Write(IndexedAtUtc.Ticks);

            writer.Write(BaseCommit);
            writer.Write(DeletedPaths.Count);
            foreach (var deleted in DeletedPaths)
            {
                writer.Write(deleted);
            }

            writer.Write(RepositoryId);
            writer.Write(GenerationId);
            writer.Write(GitTree);
            writer.Write(DirtyHash is not null);
            if (DirtyHash is not null)
            {
                writer.Write(DirtyHash);
            }

            writer.Write(Files.Count);
            foreach (var file in Files)
            {
                writer.Write(file.RelPath);
                writer.Write(file.Hash);
                writer.Write(file.ChunkStart);
                writer.Write(file.ChunkCount);
            }

            writer.Write(Chunks.Count);
            foreach (var chunk in Chunks)
            {
                writer.Write(chunk.FileIndex);
                writer.Write((byte)chunk.Kind);
                writer.Write(chunk.Symbol);
                writer.Write(chunk.Signature);
                writer.Write(chunk.Namespace);
                writer.Write(chunk.LexicalText);
                writer.Write(chunk.StartLine);
                writer.Write(chunk.EndLine);
            }

            WriteVectors(writer, Vectors);
        }

        File.Move(temp, path, overwrite: true);
    }

    // Both directions index by float element rather than by byte offset. BlockCopy's offsets are
    // byte-based ints, which would overflow at 512MB of vectors - reachable here (50k chunks x
    // 2560 dims is already 512MB), so spans do the copying instead.
    private static float[] ReadVectors(
        BinaryReader reader,
        int chunkCount,
        int dim,
        string path)
    {
        var total = (long)chunkCount * dim;
        if (total > int.MaxValue)
        {
            throw new InvalidDataException($"Index holds {total} floats, past the {int.MaxValue} array limit.");
        }

        var stream = reader.BaseStream;
        if (total * sizeof(float) > stream.Length - stream.Position)
        {
            throw Corrupt(
                path,
                $"{total} vector floats with {stream.Length - stream.Position} bytes left");
        }

        var vectors = new float[total];
        var slab = new byte[SlabBytes];
        var floatsPerSlab = SlabBytes / sizeof(float);

        var floatsRead = 0;
        while (floatsRead < total)
        {
            var floatsThisPass = (int)Math.Min(floatsPerSlab, total - floatsRead);
            var bytesWanted = floatsThisPass * sizeof(float);

            var filled = 0;
            while (filled < bytesWanted)
            {
                var read = reader.Read(slab, filled, bytesWanted - filled);
                if (read == 0)
                {
                    throw new InvalidDataException("Index truncated: vector block ended early.");
                }

                filled += read;
            }

            var floats = MemoryMarshal.Cast<byte, float>(slab.AsSpan(0, bytesWanted));
            foreach (var value in floats)
            {
                if (!float.IsFinite(value))
                {
                    // One NaN poisons every comparison it takes part in, and the ranking
                    // instability it causes looks nothing like the corruption it is.
                    throw Corrupt(path, "a non-finite vector value");
                }
            }

            floats.CopyTo(vectors.AsSpan(floatsRead, floatsThisPass));

            floatsRead += floatsThisPass;
        }

        return vectors;
    }

    private static void WriteVectors(BinaryWriter writer, float[] vectors)
    {
        var slab = new byte[SlabBytes];
        var floatsPerSlab = SlabBytes / sizeof(float);

        var written = 0;
        while (written < vectors.Length)
        {
            var floatsThisPass = Math.Min(floatsPerSlab, vectors.Length - written);
            var bytes = floatsThisPass * sizeof(float);

            MemoryMarshal.AsBytes(vectors.AsSpan(written, floatsThisPass))
                .CopyTo(slab.AsSpan(0, bytes));

            writer.Write(slab, 0, bytes);
            written += floatsThisPass;
        }
    }
}
