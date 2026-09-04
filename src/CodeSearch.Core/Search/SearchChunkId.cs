using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Resources;

namespace CodeSearch.Core.Search;

/// <summary>
/// Opaque pointer to one chunk in one exact searchable snapshot. The digest detects accidental
/// corruption and casual tampering; it is not an authentication boundary. Repository, generation,
/// tree, and dirty-overlay equality are the authorization boundary before source is read.
/// </summary>
public sealed record SearchChunkId(
    string RepositoryId,
    string GenerationId,
    string GitTree,
    string? DirtyHash,
    int Ordinal)
{
    private const int MaxFieldLength = 256;
    private const int MaxPayloadLength = 2048;
    private const int MaxPayloadSegmentLength = 2731;
    private const int DigestSegmentLength = 43;
    private const int MaxEncodedLength = 2779;
    private const int PrefixLength = 4;

    public string Encode()
    {
        ValidateFields();

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(RepositoryId);
            writer.Write(GenerationId);
            writer.Write(GitTree);
            writer.Write(DirtyHash is not null);
            if (DirtyHash is not null)
            {
                writer.Write(DirtyHash);
            }

            writer.Write(Ordinal);
        }

        var payload = stream.ToArray();
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException(
                $"The serialized chunk id payload cannot exceed {MaxPayloadLength} bytes.");
        }

        var digest = SHA256.HashData(payload);
        return $"cs1.{Base64Url(payload)}.{Base64Url(digest)}";
    }

    public static SearchChunkId Parse(string value)
    {
        if (value is null ||
            value.Length is 0 or > MaxEncodedLength ||
            string.IsNullOrWhiteSpace(value))
        {
            throw Malformed();
        }

        if (!value.StartsWith("cs1.", StringComparison.Ordinal))
        {
            throw Malformed();
        }

        var separator = value.IndexOf('.', PrefixLength);
        if (separator < 0 || value.IndexOf('.', separator + 1) >= 0)
        {
            throw Malformed();
        }

        var payloadLength = separator - PrefixLength;
        var digestLength = value.Length - separator - 1;
        if (payloadLength is 0 or > MaxPayloadSegmentLength ||
            digestLength != DigestSegmentLength)
        {
            throw Malformed();
        }

        byte[] payload;
        byte[] suppliedDigest;
        try
        {
            payload = FromBase64Url(
                value.Substring(PrefixLength, payloadLength));
            suppliedDigest = FromBase64Url(value[(separator + 1)..]);
        }
        catch (FormatException)
        {
            throw Malformed();
        }

        if (payload.Length is 0 or > MaxPayloadLength ||
            suppliedDigest.Length != SHA256.HashSizeInBytes)
        {
            throw Malformed();
        }

        var actualDigest = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, suppliedDigest))
        {
            throw new SearchChunkIdException(
                "chunk_id_tampered",
                IndexText.ChunkIdTampered);
        }

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadByte() != 1)
            {
                throw Malformed();
            }

            var parsed = new SearchChunkId(
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadBoolean() ? reader.ReadString() : null,
                reader.ReadInt32());
            if (stream.Position != stream.Length)
            {
                throw Malformed();
            }

            parsed.ValidateFields();
            return parsed;
        }
        catch (SearchChunkIdException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is EndOfStreamException or IOException or
            ArgumentException or ArgumentOutOfRangeException)
        {
            throw Malformed();
        }
    }

    private void ValidateFields()
    {
        ValidateRequired(RepositoryId, nameof(RepositoryId));
        ValidateRequired(GenerationId, nameof(GenerationId));
        ValidateRequired(GitTree, nameof(GitTree));
        if (DirtyHash is { Length: > MaxFieldLength })
        {
            throw new ArgumentException(
                $"DirtyHash cannot exceed {MaxFieldLength} characters.",
                nameof(DirtyHash));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(Ordinal);
    }

    private static void ValidateRequired(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > MaxFieldLength)
        {
            throw new ArgumentException(
                $"{name} cannot exceed {MaxFieldLength} characters.",
                name);
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        if (value.Length == 0 ||
            value.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            throw new FormatException();
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException()
        };
        var decoded = Convert.FromBase64String(base64);
        if (!string.Equals(value, Base64Url(decoded), StringComparison.Ordinal))
        {
            throw new FormatException();
        }

        return decoded;
    }

    private static SearchChunkIdException Malformed() =>
        new("chunk_id_malformed", IndexText.ChunkIdMalformed);
}

public sealed class SearchChunkIdException(string code, string message)
    : FormatException(message)
{
    public string Code { get; } = code;
}

public sealed class SearchChunkResolutionException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class SearchChunkResolver
{
    public static void ValidateSnapshot(SearchChunkId expected, SearchChunkId actual)
    {
        if (!string.Equals(
                expected.RepositoryId,
                actual.RepositoryId,
                StringComparison.Ordinal))
        {
            throw Mismatch("wrong_repository", IndexText.ChunkWrongRepository);
        }

        if (!string.Equals(
                expected.GenerationId,
                actual.GenerationId,
                StringComparison.Ordinal))
        {
            throw Mismatch("stale_generation", IndexText.ChunkStaleGeneration);
        }

        if (!string.Equals(expected.GitTree, actual.GitTree, StringComparison.Ordinal))
        {
            throw Mismatch("stale_worktree", IndexText.ChunkStaleWorktree);
        }

        if (!string.Equals(expected.DirtyHash, actual.DirtyHash, StringComparison.Ordinal))
        {
            throw Mismatch("stale_overlay", IndexText.ChunkStaleOverlay);
        }
    }

    public static void ValidateOrdinal(SearchChunkId id, int chunkCount)
    {
        if (id.Ordinal < 0 || id.Ordinal >= chunkCount)
        {
            throw Mismatch("chunk_out_of_range", IndexText.ChunkOutOfRange);
        }
    }

    /// <summary>
    /// The one place a chunk refusal is composed: the code, then the sentence.
    ///
    /// The code stays Latin and is what a caller branches on; the sentence follows the reader.
    /// Composing here rather than in each message keeps the code out of the catalogue, where it
    /// would have become a token inside a translated string and a second spelling of a fact the
    /// exception already carries.
    /// </summary>
    public static SearchChunkResolutionException Mismatch(
        string code,
        string message) =>
        new(code, $"{code}: {message}");
}
