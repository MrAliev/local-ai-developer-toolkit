using System.Security.Cryptography;
using System.Text.Json;
using CodeSearch.Core.Semantics;
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
        string? sourceSemanticIndexPath = null,
        DateTimeOffset? publishedAtUtc = null,
        IReadOnlyList<SemanticAdapterStatus>? semanticAdapterStatuses = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIndexPath);
        ArgumentNullException.ThrowIfNull(identity);
        Directory.CreateDirectory(_generationsRoot);
        var target = Path.Combine(_generationsRoot, identity.Id);
        if (Directory.Exists(target))
        {
            var existing = ReadManifest(identity.Id);
            if (sourceSemanticIndexPath is not null && existing.SemanticIndexFile is null)
            {
                throw new InvalidOperationException(
                    "The immutable generation already exists without a semantic index.");
            }

            return existing;
        }

        if ((sourceSemanticIndexPath is null) != (identity.SemanticIndexVersion == 0))
        {
            throw new InvalidOperationException(
                "Semantic index input and generation semantic format version must be supplied together.");
        }

        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            const string indexFile = "base.cidx";
            var stagedIndex = Path.Combine(staging, indexFile);
            File.Copy(sourceIndexPath, stagedIndex, overwrite: false);
            var checksum = Checksum(stagedIndex);
            string? semanticIndexFile = null;
            string? semanticChecksum = null;
            if (sourceSemanticIndexPath is not null)
            {
                semanticIndexFile = "semantic.sidx";
                var stagedSemanticIndex = Path.Combine(staging, semanticIndexFile);
                File.Copy(sourceSemanticIndexPath, stagedSemanticIndex, overwrite: false);
                semanticChecksum = Checksum(stagedSemanticIndex);
            }

            var manifest = new GenerationManifest(
                identity,
                indexFile,
                checksum,
                publishedAtUtc ?? DateTimeOffset.UtcNow,
                semanticIndexFile,
                semanticChecksum,
                semanticAdapterStatuses);
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

        if ((manifest.SemanticIndexFile is null) != (manifest.SemanticIndexChecksum is null) ||
            (manifest.SemanticIndexFile is null) != (manifest.Identity.SemanticIndexVersion == 0))
        {
            throw new InvalidDataException(
                "Generation semantic index metadata is inconsistent with its identity.");
        }

        if (manifest.SemanticIndexFile is not null)
        {
            var semanticPath = Path.Combine(directory, manifest.SemanticIndexFile);
            if (!File.Exists(semanticPath) ||
                !string.Equals(
                    Checksum(semanticPath),
                    manifest.SemanticIndexChecksum,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Generation semantic index checksum does not match.");
            }
        }

        if (manifest.SemanticAdapters is not null &&
            (manifest.SemanticAdapters.Any(status =>
                 string.IsNullOrWhiteSpace(status.Name) ||
                 !Enum.IsDefined(status.State) ||
                 status.DurationMilliseconds < 0) ||
             manifest.SemanticAdapters.Select(status => status.Name)
                 .Distinct(StringComparer.Ordinal).Count() != manifest.SemanticAdapters.Count))
        {
            throw new InvalidDataException("Generation semantic adapter metadata is invalid.");
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

    public string SemanticIndexPath(string generationId) =>
        Path.Combine(_generationsRoot, generationId, "semantic.sidx");

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
