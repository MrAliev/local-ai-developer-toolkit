using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Releases;

public sealed partial class ReleaseManifestVerifier : IDisposable
{
    private static readonly BigInteger P256Order = new(
        Convert.FromHexString(
            "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        isUnsigned: true,
        isBigEndian: true);
    private const int MaximumManifestBytes = 1024 * 1024;
    internal const long MaximumPackageSize = 4L * 1024 * 1024 * 1024;
    private const long MaximumModelSize = 1024L * 1024 * 1024 * 1024;
    private readonly ECDsa publicKey;

    public ReleaseManifestVerifier(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        try
        {
            publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
            var parameters = publicKey.ExportParameters(includePrivateParameters: false);
            if (read != subjectPublicKeyInfo.Length ||
                parameters.Q.X?.Length != 32 ||
                parameters.Q.Y?.Length != 32 ||
                !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                publicKey.Dispose();
                throw new ArgumentException(
                    "The release key must be an ECDSA P-256 SubjectPublicKeyInfo key.",
                    nameof(subjectPublicKeyInfo));
            }
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException(
                "The release key must be an ECDSA P-256 SubjectPublicKeyInfo key.",
                nameof(subjectPublicKeyInfo),
                exception);
        }
    }

    public ReleaseManifest Verify(
        ReadOnlySpan<byte> manifestJson,
        ReadOnlySpan<byte> signature)
    {
        try
        {
            if (manifestJson.IsEmpty || manifestJson.Length > MaximumManifestBytes ||
                HasUtf8Bom(manifestJson) ||
                !IsCanonicalP256Signature(signature))
            {
                throw InvalidManifest();
            }

            var manifest = Parse(manifestJson);
            var canonical = CreateCanonicalUnsignedPayload(manifest);
            if (!manifestJson.SequenceEqual(canonical) ||
                !publicKey.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw InvalidManifest();
            }

            if (manifest.SchemaVersion != 1 ||
                manifest.ProtocolVersion != BrokerCompatibilityContract.ProtocolVersion ||
                !string.Equals(
                    manifest.BuildCompatibilityId,
                    BrokerCompatibilityContract.BuildCompatibilityId,
                    StringComparison.Ordinal))
            {
                throw new ReleaseVerificationException(
                    "The release is not compatible with this installer.");
            }

            return manifest;
        }
        catch (ReleaseVerificationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
            InvalidOperationException or FormatException or
            OverflowException or CryptographicException or DecoderFallbackException)
        {
            throw InvalidManifest();
        }
    }

    public static byte[] CreateCanonicalUnsignedPayload(ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateStructure(manifest);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", manifest.SchemaVersion);
            writer.WriteString("ReleaseVersion", manifest.ReleaseVersion);
            writer.WriteString("VersionDirectory", manifest.VersionDirectory);
            writer.WriteString("ModelCatalogVersion", manifest.ModelCatalogVersion);
            writer.WriteNumber("ProtocolVersion", manifest.ProtocolVersion);
            writer.WriteString("BuildCompatibilityId", manifest.BuildCompatibilityId);
            writer.WriteString("PackageUri", manifest.PackageUri.AbsoluteUri);
            writer.WriteNumber("PackageSize", manifest.PackageSize);
            writer.WriteString("PackageSha256", manifest.PackageSha256);
            writer.WriteBoolean("RequiresAuthenticode", manifest.RequiresAuthenticode);
            writer.WritePropertyName("Models");
            writer.WriteStartArray();
            foreach (var model in manifest.Models)
            {
                writer.WriteStartObject();
                writer.WriteString("Name", model.Name);
                writer.WriteNumber("ContextTokens", model.ContextTokens);
                writer.WriteNumber("DownloadSize", model.DownloadSize);
                writer.WriteNumber("EstimatedVramBytes", model.EstimatedVramBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static ReleaseManifest Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var root = document.RootElement;
        RequireObject(root);
        RequireExactProperties(
            root,
            "SchemaVersion", "ReleaseVersion", "VersionDirectory",
            "ModelCatalogVersion",
            "ProtocolVersion", "BuildCompatibilityId", "PackageUri",
            "PackageSize", "PackageSha256", "RequiresAuthenticode", "Models");

        var modelsElement = root.GetProperty("Models");
        if (modelsElement.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest();
        }

        var models = new List<ManifestModel>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            RequireObject(item);
            RequireExactProperties(
                item,
                "Name", "ContextTokens", "DownloadSize", "EstimatedVramBytes");
            models.Add(new ManifestModel(
                RequireString(item, "Name"),
                RequireInt32(item, "ContextTokens"),
                RequireInt64(item, "DownloadSize"),
                RequireInt64(item, "EstimatedVramBytes")));
        }

        var uriText = RequireString(root, "PackageUri");
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            throw InvalidManifest();
        }

        return new ReleaseManifest(
            RequireInt32(root, "SchemaVersion"),
            RequireString(root, "ReleaseVersion"),
            RequireString(root, "VersionDirectory"),
            RequireString(root, "ModelCatalogVersion"),
            RequireInt32(root, "ProtocolVersion"),
            RequireString(root, "BuildCompatibilityId"),
            uri,
            RequireInt64(root, "PackageSize"),
            RequireString(root, "PackageSha256"),
            RequireBoolean(root, "RequiresAuthenticode"),
            models);
    }

    private static void ValidateStructure(ReleaseManifest manifest)
    {
        if (manifest.SchemaVersion <= 0 ||
            manifest.ReleaseVersion.Length > 128 ||
            !SafeReleaseVersion().IsMatch(manifest.ReleaseVersion) ||
            !SafeVersionDirectory().IsMatch(manifest.VersionDirectory) ||
            manifest.VersionDirectory is "." or ".." ||
            !SafeCatalogVersion().IsMatch(manifest.ModelCatalogVersion) ||
            manifest.ProtocolVersion <= 0 ||
            string.IsNullOrWhiteSpace(manifest.BuildCompatibilityId) ||
            manifest.BuildCompatibilityId.Length > 128 ||
            !SafeCompatibilityId().IsMatch(manifest.BuildCompatibilityId) ||
            !manifest.PackageUri.IsAbsoluteUri ||
            manifest.PackageUri.AbsoluteUri.Length > 2048 ||
            string.IsNullOrEmpty(manifest.PackageUri.Host) ||
            !string.Equals(manifest.PackageUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(manifest.PackageUri.UserInfo) ||
            !string.IsNullOrEmpty(manifest.PackageUri.Fragment) ||
            manifest.PackageSize is <= 0 or > MaximumPackageSize ||
            !CanonicalSha256().IsMatch(manifest.PackageSha256) ||
            manifest.Models.Count > 128)
        {
            throw InvalidManifest();
        }

        var modelOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelFamilies = new Dictionary<
            string,
            (string Name, long DownloadSize, long EstimatedVramBytes)>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var model in manifest.Models)
        {
            var normalizedName = model?.Name?.Normalize(NormalizationForm.FormC) ?? string.Empty;
            var optionKey = string.Concat(
                normalizedName,
                "\u001F",
                model?.ContextTokens.ToString(CultureInfo.InvariantCulture));
            if (model is null ||
                string.IsNullOrWhiteSpace(model.Name) ||
                !SafeModelName().IsMatch(model.Name) ||
                !modelOptions.Add(optionKey) ||
                model.Name != normalizedName ||
                !IsSupportedContext(model.ContextTokens) ||
                model.DownloadSize is <= 0 or > MaximumModelSize ||
                model.EstimatedVramBytes is <= 0 or > MaximumModelSize)
            {
                throw InvalidManifest();
            }

            if (modelFamilies.TryGetValue(normalizedName, out var family))
            {
                if (!string.Equals(family.Name, model.Name, StringComparison.Ordinal) ||
                    family.DownloadSize != model.DownloadSize ||
                    family.EstimatedVramBytes != model.EstimatedVramBytes)
                {
                    throw InvalidManifest();
                }
            }
            else
            {
                modelFamilies.Add(
                    normalizedName,
                    (model.Name, model.DownloadSize, model.EstimatedVramBytes));
            }
        }
    }

    private static bool IsSupportedContext(int value) =>
        value is >= 2048 and <= 262144 && (value & (value - 1)) == 0;

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value) =>
        value.Length >= 3 && value[0] == 0xEF && value[1] == 0xBB && value[2] == 0xBF;

    private static bool IsCanonicalP256Signature(ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64)
        {
            return false;
        }

        var r = new BigInteger(
            signature[..32],
            isUnsigned: true,
            isBigEndian: true);
        var s = new BigInteger(
            signature[32..],
            isUnsigned: true,
            isBigEndian: true);
        return r > BigInteger.Zero && r < P256Order &&
            s > BigInteger.Zero && s <= P256Order / 2;
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest();
        }
    }

    private static void RequireExactProperties(
        JsonElement value,
        params string[] expected)
    {
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                throw InvalidManifest();
            }
        }

        if (remaining.Count != 0)
        {
            throw InvalidManifest();
        }
    }

    private static string RequireString(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidManifest();
        }

        return property.GetString() ?? throw InvalidManifest();
    }

    private static int RequireInt32(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var result)
            ? result
            : throw InvalidManifest();
    }

    private static long RequireInt64(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var result)
            ? result
            : throw InvalidManifest();
    }

    private static bool RequireBoolean(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidManifest(),
        };
    }

    private static ReleaseVerificationException InvalidManifest() =>
        new("Release verification failed.");

    public void Dispose() => publicKey.Dispose();

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2}(?:-[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?)?$")]
    private static partial Regex SafeReleaseVersion();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$")]
    private static partial Regex SafeVersionDirectory();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$")]
    private static partial Regex SafeCompatibilityId();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$")]
    private static partial Regex SafeCatalogVersion();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$")]
    private static partial Regex SafeModelName();

    [GeneratedRegex("^[0-9A-F]{64}$")]
    private static partial Regex CanonicalSha256();
}
