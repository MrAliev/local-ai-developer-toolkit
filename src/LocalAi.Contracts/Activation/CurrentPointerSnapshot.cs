using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAi.Contracts.Activation;

public sealed class CurrentPointerSnapshot
{
    public const int MaximumBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private CurrentPointerSnapshot(
        bool exists,
        string? version,
        byte[] rawBytes,
        byte[] sha256,
        bool canonical)
    {
        Exists = exists;
        Version = version;
        RawBytes = Array.AsReadOnly(rawBytes.ToArray());
        Sha256 = Array.AsReadOnly(sha256.ToArray());
        Sha256Hex = Convert.ToHexString(sha256);
        IsCanonical = canonical;
    }

    public bool Exists { get; }
    public string? Version { get; }
    public IReadOnlyList<byte> RawBytes { get; }
    public IReadOnlyList<byte> Sha256 { get; }
    public string Sha256Hex { get; }
    public bool IsCanonical { get; }

    public static CurrentPointerSnapshot Read(ActivationExclusiveLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!File.Exists(lease.CurrentPath))
        {
            return new(false, null, [], SHA256.HashData([]), canonical: true);
        }

        byte[] bytes;
        using (var stream = new FileStream(
                   lease.CurrentPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            if (stream.Length is < 1 or > MaximumBytes)
            {
                throw Invalid();
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw Invalid();
            }
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw Invalid();
        }

        var json = StrictUtf8.GetString(bytes);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 2,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }

        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 2 ||
            properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            !properties.Any(property => property.NameEquals("schemaVersion")) ||
            !properties.Any(property => property.NameEquals("version")) ||
            root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
            root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("version").ValueKind != JsonValueKind.String)
        {
            throw Invalid();
        }

        var version = root.GetProperty("version").GetString();
        if (!LocalAiVersionName.IsSafe(version))
        {
            throw Invalid();
        }

        var canonicalBytes = CreateCanonicalBytes(version!);
        return new(
            true,
            version,
            bytes,
            SHA256.HashData(bytes),
            bytes.AsSpan().SequenceEqual(canonicalBytes));
    }

    public static byte[] CreateCanonicalBytes(string version)
    {
        if (!LocalAiVersionName.IsSafe(version))
        {
            throw new ArgumentException("The LocalAi version is unsafe.", nameof(version));
        }

        return JsonSerializer.SerializeToUtf8Bytes(new PointerDocument(1, version));
    }

    private static CurrentPointerException Invalid() =>
        new("The current-version pointer is invalid.");

    private sealed record PointerDocument(
        [property: System.Text.Json.Serialization.JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("version")] string Version);
}

public sealed class CurrentPointerExpectation
{
    private readonly byte[]? expectedSha256;

    private CurrentPointerExpectation(bool missing, byte[]? expectedSha256)
    {
        RequiresMissing = missing;
        this.expectedSha256 = expectedSha256?.ToArray();
    }

    public static CurrentPointerExpectation Missing { get; } = new(true, null);
    public bool RequiresMissing { get; }
    public string? ExpectedSha256Hex => expectedSha256 is null
        ? null
        : Convert.ToHexString(expectedSha256);

    public static CurrentPointerExpectation ExactSha256(ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("SHA-256 must contain exactly 32 bytes.", nameof(sha256));
        }

        return new(false, sha256.ToArray());
    }

    public void Validate(CurrentPointerSnapshot actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var matches = RequiresMissing
            ? !actual.Exists
            : actual.Exists && CryptographicOperations.FixedTimeEquals(
                expectedSha256!,
                actual.Sha256.ToArray());
        if (!matches)
        {
            throw new CurrentPointerChangedException(
                "The current-version pointer changed before activation.");
        }
    }
}

public static class LocalAiVersionName
{
    private static readonly HashSet<string> Reserved = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value is "." or ".." || !AlphaNumeric(value[0]) ||
            !AlphaNumeric(value[^1]) || Reserved.Contains(value.Split('.')[0]))
        {
            return false;
        }

        return value.All(character =>
            AlphaNumeric(character) || character is '.' or '_' or '-');
    }

    private static bool AlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}

public sealed class CurrentPointerChangedException(string message) : Exception(message);
public sealed class CurrentPointerException(string message) : Exception(message);
