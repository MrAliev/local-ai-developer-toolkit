using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

public sealed class GenerationStore
{
    private readonly string _root;
    private readonly string _generationsRoot;
    private readonly string _currentPath;

    public GenerationStore(string repositoryRuntimeRoot)
    {
        _root = Path.GetFullPath(repositoryRuntimeRoot);
        _generationsRoot = Path.Combine(_root, "generations");
        _currentPath = Path.Combine(_root, "current.json");
    }

    public GenerationManifest PublishIndex(
        string sourceIndexPath,
        GenerationIdentity identity,
        DateTimeOffset? publishedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIndexPath);
        ArgumentNullException.ThrowIfNull(identity);
        Directory.CreateDirectory(_generationsRoot);
        var target = Path.Combine(_generationsRoot, identity.Id);
        if (Directory.Exists(target))
        {
            return ReadManifest(identity.Id);
        }

        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            const string indexFile = "base.cidx";
            var stagedIndex = Path.Combine(staging, indexFile);
            File.Copy(sourceIndexPath, stagedIndex, overwrite: false);
            var checksum = Checksum(stagedIndex);
            var manifest = new GenerationManifest(
                identity,
                indexFile,
                checksum,
                publishedAtUtc ?? DateTimeOffset.UtcNow);
            AtomicWriteJson(Path.Combine(staging, "manifest.json"), manifest);
            Directory.Move(staging, target);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public GenerationManifest ReadManifest(string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        var directory = Path.Combine(_generationsRoot, generationId);
        var manifest = JsonSerializer.Deserialize<GenerationManifest>(
            File.ReadAllText(Path.Combine(directory, "manifest.json")),
            LocalAiJson.Strict)
            ?? throw new InvalidDataException("Generation manifest is empty.");
        if (manifest.Identity.Id != generationId)
        {
            throw new InvalidDataException("Generation identity does not match its directory.");
        }

        var indexPath = Path.Combine(directory, manifest.IndexFile);
        if (!File.Exists(indexPath) ||
            !string.Equals(
                Checksum(indexPath),
                manifest.IndexChecksum,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generation index checksum does not match.");
        }

        return manifest;
    }

    public GenerationPointer? ReadCurrent() =>
        File.Exists(_currentPath)
            ? JsonSerializer.Deserialize<GenerationPointer>(
                File.ReadAllText(_currentPath),
                LocalAiJson.Strict)
            : null;

    public void SetCurrent(GenerationManifest generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ReadManifest(generation.Identity.Id);
        AtomicWriteJson(
            _currentPath,
            new GenerationPointer(
                generation.Identity.Id,
                generation.Identity.DevTree,
                DateTimeOffset.UtcNow));
    }

    public string IndexPath(string generationId) =>
        Path.Combine(_generationsRoot, generationId, "base.cidx");

    private static string Checksum(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AtomicWriteJson<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(value, LocalAiJson.Strict));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
