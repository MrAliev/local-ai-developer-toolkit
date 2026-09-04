using System.Text;
using CodeSearch.Core.Resources;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// Snapshot-bound replacement slices for documents whose text or resolved semantics changed.
/// Deleted documents are represented explicitly as tombstones.
/// </summary>
public sealed record SemanticIndexOverlay
{
    public const int CurrentVersion = 1;

    private const string Magic = "SOVL";
    private const int MaximumTombstones = 10_000_000;

    public required string RepositoryId { get; init; }
    public required string GenerationId { get; init; }
    public required string BaseGitTree { get; init; }
    public required string GitTree { get; init; }
    public string? DirtyHash { get; init; }
    public required List<string> DeletedDocuments { get; init; }
    public required SemanticIndex Changed { get; init; }

    public static SemanticIndexOverlay Create(
        SemanticIndex baseIndex,
        SemanticIndex currentIndex,
        IEnumerable<string> dirtyPaths)
    {
        ArgumentNullException.ThrowIfNull(baseIndex);
        ArgumentNullException.ThrowIfNull(currentIndex);
        ArgumentNullException.ThrowIfNull(dirtyPaths);
        if (!string.Equals(baseIndex.RepositoryId, currentIndex.RepositoryId, StringComparison.Ordinal) ||
            !string.Equals(baseIndex.GenerationId, currentIndex.GenerationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Semantic overlay indexes belong to different generations.");
        }

        var baseDocuments = baseIndex.Documents.ToDictionary(
            document => NormalizePath(document.RelPath), StringComparer.Ordinal);
        var currentDocuments = currentIndex.Documents.ToDictionary(
            document => NormalizePath(document.RelPath), StringComparer.Ordinal);
        var allPaths = baseDocuments.Keys.Concat(currentDocuments.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var affected = dirtyPaths.Select(NormalizePath)
            .Where(allPaths.Contains)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in allPaths)
        {
            if (!SameDocument(baseDocuments.GetValueOrDefault(path), currentDocuments.GetValueOrDefault(path)) ||
                !SameOccurrences(baseIndex, currentIndex, path))
            {
                affected.Add(path);
            }
        }

        var deleted = affected.Where(path =>
                baseDocuments.ContainsKey(path) && !currentDocuments.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var changedDocuments = affected.Where(currentDocuments.ContainsKey)
            .Select(path => currentDocuments[path])
            .OrderBy(document => document.RelPath, StringComparer.Ordinal)
            .ToList();
        var changedOccurrences = currentIndex.Occurrences
            .Where(occurrence => affected.Contains(NormalizePath(occurrence.DocumentPath)))
            .ToList();
        var impactedSymbols = baseIndex.Occurrences.Concat(currentIndex.Occurrences)
            .Where(occurrence => affected.Contains(NormalizePath(occurrence.DocumentPath)))
            .Select(occurrence => occurrence.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        var relationships = currentIndex.Relationships.Where(relationship =>
                impactedSymbols.Contains(relationship.SourceSymbolId) ||
                impactedSymbols.Contains(relationship.TargetSymbolId))
            .ToList();
        var requiredSymbols = changedOccurrences.Select(occurrence => occurrence.SymbolId)
            .Concat(relationships.SelectMany(relationship =>
                new[] { relationship.SourceSymbolId, relationship.TargetSymbolId }))
            .ToHashSet(StringComparer.Ordinal);
        var currentSymbols = currentIndex.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        var baseSymbols = baseIndex.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        var symbols = requiredSymbols.Select(id =>
                currentSymbols.GetValueOrDefault(id) ?? baseSymbols.GetValueOrDefault(id) ??
                throw new InvalidDataException($"Semantic overlay symbol '{id}' is unavailable."))
            .ToList();
        var changed = new SemanticIndex
        {
            RepositoryId = currentIndex.RepositoryId,
            GenerationId = currentIndex.GenerationId,
            GitTree = currentIndex.GitTree,
            DirtyHash = currentIndex.DirtyHash,
            BaseCommit = currentIndex.BaseCommit,
            IndexedAtUtc = currentIndex.IndexedAtUtc,
            Documents = changedDocuments,
            Symbols = symbols,
            Occurrences = changedOccurrences,
            Relationships = relationships,
        }.NormalizeForUse();

        return new SemanticIndexOverlay
        {
            RepositoryId = currentIndex.RepositoryId,
            GenerationId = currentIndex.GenerationId,
            BaseGitTree = baseIndex.GitTree,
            GitTree = currentIndex.GitTree,
            DirtyHash = currentIndex.DirtyHash,
            DeletedDocuments = deleted,
            Changed = changed,
        }.Validate();
    }

    public SemanticIndex Materialize(SemanticIndex baseIndex)
    {
        ArgumentNullException.ThrowIfNull(baseIndex);
        var overlay = Validate();
        if (!string.Equals(baseIndex.RepositoryId, RepositoryId, StringComparison.Ordinal) ||
            !string.Equals(baseIndex.GenerationId, GenerationId, StringComparison.Ordinal) ||
            !string.Equals(baseIndex.GitTree, BaseGitTree, StringComparison.Ordinal))
        {
            throw new SemanticSnapshotMismatchException(IndexText.OverlayBaseMismatch);
        }

        var touched = DeletedDocuments.Concat(Changed.Documents.Select(document => document.RelPath))
            .ToHashSet(StringComparer.Ordinal);
        var impacted = baseIndex.Occurrences.Concat(Changed.Occurrences)
            .Where(occurrence => touched.Contains(occurrence.DocumentPath))
            .Select(occurrence => occurrence.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        var documents = baseIndex.Documents.Where(document => !touched.Contains(document.RelPath))
            .Concat(Changed.Documents)
            .ToList();
        var occurrences = baseIndex.Occurrences
            .Where(occurrence => !touched.Contains(occurrence.DocumentPath))
            .Concat(Changed.Occurrences)
            .ToList();
        var relationships = baseIndex.Relationships.Where(relationship =>
                !impacted.Contains(relationship.SourceSymbolId) &&
                !impacted.Contains(relationship.TargetSymbolId))
            .Concat(Changed.Relationships)
            .ToList();
        var symbols = baseIndex.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        foreach (var symbol in Changed.Symbols)
        {
            symbols[symbol.Id] = symbol;
        }

        var requiredSymbols = occurrences.Select(occurrence => occurrence.SymbolId)
            .Concat(relationships.SelectMany(relationship =>
                new[] { relationship.SourceSymbolId, relationship.TargetSymbolId }))
            .ToHashSet(StringComparer.Ordinal);
        return new SemanticIndex
        {
            RepositoryId = RepositoryId,
            GenerationId = GenerationId,
            GitTree = GitTree,
            DirtyHash = DirtyHash,
            BaseCommit = Changed.BaseCommit,
            IndexedAtUtc = Changed.IndexedAtUtc,
            Documents = documents,
            Symbols = symbols.Values.Where(symbol => requiredSymbols.Contains(symbol.Id)).ToList(),
            Occurrences = occurrences,
            Relationships = relationships,
        }.NormalizeForUse();
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var overlay = Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Encoding.ASCII.GetBytes(Magic));
                writer.Write(CurrentVersion);
                writer.Write(overlay.RepositoryId);
                writer.Write(overlay.GenerationId);
                writer.Write(overlay.BaseGitTree);
                writer.Write(overlay.GitTree);
                writer.Write(overlay.DirtyHash is not null);
                if (overlay.DirtyHash is not null) writer.Write(overlay.DirtyHash);
                writer.Write(overlay.DeletedDocuments.Count);
                foreach (var deleted in overlay.DeletedDocuments) writer.Write(deleted);
                overlay.Changed.WriteTo(writer);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static SemanticIndexOverlay Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{path}' is not a semantic overlay.");
        }

        var version = reader.ReadInt32();
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Semantic overlay '{path}' has unsupported version {version}.");
        }

        var repositoryId = reader.ReadString();
        var generationId = reader.ReadString();
        var baseGitTree = reader.ReadString();
        var gitTree = reader.ReadString();
        var dirtyHash = reader.ReadBoolean() ? reader.ReadString() : null;
        var count = reader.ReadInt32();
        if (count is < 0 or > MaximumTombstones)
        {
            throw new InvalidDataException("Semantic overlay tombstone count is invalid.");
        }

        var deleted = new List<string>(count);
        for (var index = 0; index < count; index++) deleted.Add(reader.ReadString());
        var changed = SemanticIndex.ReadFrom(reader, path, requireEnd: false);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Semantic overlay has trailing data.");
        }

        return new SemanticIndexOverlay
        {
            RepositoryId = repositoryId,
            GenerationId = generationId,
            BaseGitTree = baseGitTree,
            GitTree = gitTree,
            DirtyHash = dirtyHash,
            DeletedDocuments = deleted,
            Changed = changed,
        }.Validate();
    }

    private SemanticIndexOverlay Validate()
    {
        if (string.IsNullOrWhiteSpace(RepositoryId) || string.IsNullOrWhiteSpace(GenerationId) ||
            string.IsNullOrWhiteSpace(BaseGitTree) || string.IsNullOrWhiteSpace(GitTree))
        {
            throw new InvalidDataException("Semantic overlay identity is incomplete.");
        }

        var deleted = DeletedDocuments.Select(NormalizePath)
            .OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (deleted.Distinct(StringComparer.Ordinal).Count() != deleted.Count ||
            deleted.Intersect(Changed.Documents.Select(document => document.RelPath), StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("Semantic overlay document replacements are inconsistent.");
        }

        if (!string.Equals(Changed.RepositoryId, RepositoryId, StringComparison.Ordinal) ||
            !string.Equals(Changed.GenerationId, GenerationId, StringComparison.Ordinal) ||
            !string.Equals(Changed.GitTree, GitTree, StringComparison.Ordinal) ||
            !string.Equals(Changed.DirtyHash, DirtyHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Semantic overlay payload identity is inconsistent.");
        }

        return this with { DeletedDocuments = deleted, Changed = Changed.NormalizeForUse() };
    }

    private static bool SameDocument(SemanticDocument? left, SemanticDocument? right) =>
        left is null && right is null ||
        left is not null && right is not null && left.Hash.AsSpan().SequenceEqual(right.Hash);

    private static bool SameOccurrences(SemanticIndex left, SemanticIndex right, string path)
    {
        var leftSet = left.Occurrences.Where(value => value.DocumentPath == path).ToHashSet();
        var rightSet = right.Occurrences.Where(value => value.DocumentPath == path).ToHashSet();
        return leftSet.SetEquals(rightSet);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Semantic overlay path '{path}' is invalid.");
        }

        return normalized;
    }
}
