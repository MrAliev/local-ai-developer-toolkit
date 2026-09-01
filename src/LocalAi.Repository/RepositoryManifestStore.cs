using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Repository;

public sealed class RepositoryManifestStore
{
    private readonly string _manifestPath;

    public RepositoryManifestStore(FsPath repositoryRuntimeRoot)
    {
        _manifestPath = repositoryRuntimeRoot.Combine("manifest.json").Value;
    }

    public void Save(RepositoryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(
            Path.GetDirectoryName(_manifestPath)
            ?? throw new InvalidOperationException("Manifest directory is unavailable."));
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            LocalAiJson.Strict);
        var document = new ManifestDocument(
            1,
            manifest,
            Convert.ToHexString(SHA256.HashData(payload)));
        var temporary = _manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(document, LocalAiJson.Strict));
            File.Move(temporary, _manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public RepositoryManifest? Read()
    {
        if (!File.Exists(_manifestPath))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<ManifestDocument>(
            File.ReadAllText(_manifestPath),
            LocalAiJson.Strict)
            ?? throw new InvalidDataException("Repository manifest is empty.");
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException("Repository manifest schema is unsupported.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            document.Manifest,
            LocalAiJson.Strict);
        byte[] declared;
        if (document.Checksum.Length != 64)
        {
            throw new InvalidDataException("Repository manifest checksum is malformed.");
        }

        try
        {
            declared = Convert.FromHexString(document.Checksum);
        }
        catch (FormatException error)
        {
            // A checksum that does not decode is the same category as one that does not
            // match — a corrupt manifest, answered by rebuilding it — not a raw
            // FormatException that reads as a bug in this code (#209/m7).
            throw new InvalidDataException(
                "Repository manifest checksum is malformed.",
                error);
        }

        if (!CryptographicOperations.FixedTimeEquals(declared, SHA256.HashData(payload)))
        {
            throw new InvalidDataException("Repository manifest checksum does not match.");
        }

        return document.Manifest;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ManifestDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] RepositoryManifest Manifest,
        [property: JsonRequired] string Checksum);
}
