using System.Text;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// A deterministic, vector-free semantic index for exact definitions and references.
/// It is deliberately separate from CIDX so navigation data cannot change retrieval ordinals.
/// </summary>
public sealed record SemanticIndex
{
    public const int CurrentVersion = 1;

    private const string Magic = "SIDX";
    private const int MaximumEntries = 10_000_000;

    public required string RepositoryId { get; init; }
    public required string GenerationId { get; init; }
    public required string GitTree { get; init; }
    public string? DirtyHash { get; init; }
    public string BaseCommit { get; init; } = string.Empty;
    public required DateTime IndexedAtUtc { get; init; }
    public required List<SemanticDocument> Documents { get; init; }
    public required List<SemanticSymbol> Symbols { get; init; }
    public required List<SemanticOccurrence> Occurrences { get; init; }
    public required List<SemanticRelationship> Relationships { get; init; }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = NormalizeAndValidate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException($"Cannot resolve the directory for semantic index '{path}'.");
        Directory.CreateDirectory(directory);

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                normalized.WriteTo(writer);
            }

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

    public static SemanticIndex Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return ReadFrom(reader, path, requireEnd: true);
    }

    internal static SemanticIndex ReadFrom(
        BinaryReader reader,
        string origin,
        bool requireEnd)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var stream = reader.BaseStream;

        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{origin}' is not a semantic index (magic '{magic}').");
        }

        var version = reader.ReadInt32();
        if (version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Semantic index '{origin}' is version {version}; this build reads version {CurrentVersion}.");
        }

        var repositoryId = reader.ReadString();
        var generationId = reader.ReadString();
        var gitTree = reader.ReadString();
        var dirtyHash = reader.ReadBoolean() ? reader.ReadString() : null;
        var baseCommit = reader.ReadString();
        var indexedAtUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);

        var documentCount = ReadCount(reader, "document");
        var documents = new List<SemanticDocument>(documentCount);
        for (var i = 0; i < documentCount; i++)
        {
            documents.Add(new SemanticDocument
            {
                RelPath = reader.ReadString(),
                Hash = ReadHash(reader),
            });
        }

        var symbolCount = ReadCount(reader, "symbol");
        var symbols = new List<SemanticSymbol>(symbolCount);
        for (var i = 0; i < symbolCount; i++)
        {
            symbols.Add(new SemanticSymbol
            {
                Id = reader.ReadString(),
                DisplayName = reader.ReadString(),
                Kind = (SemanticSymbolKind)reader.ReadByte(),
                Signature = reader.ReadString(),
                Documentation = reader.ReadBoolean() ? reader.ReadString() : null,
            });
        }

        var occurrenceCount = ReadCount(reader, "occurrence");
        var occurrences = new List<SemanticOccurrence>(occurrenceCount);
        for (var i = 0; i < occurrenceCount; i++)
        {
            var documentIndex = ReadIndex(reader, documentCount, "document");
            var symbolIndex = ReadIndex(reader, symbolCount, "symbol");
            occurrences.Add(new SemanticOccurrence
            {
                DocumentPath = documents[documentIndex].RelPath,
                SymbolId = symbols[symbolIndex].Id,
                Range = new SourceRange(
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32()),
                Roles = (SemanticOccurrenceRoles)reader.ReadUInt16(),
                Precision = (NavigationPrecision)reader.ReadByte(),
            });
        }

        var relationshipCount = ReadCount(reader, "relationship");
        var relationships = new List<SemanticRelationship>(relationshipCount);
        for (var i = 0; i < relationshipCount; i++)
        {
            relationships.Add(new SemanticRelationship
            {
                SourceSymbolId = symbols[ReadIndex(reader, symbolCount, "source symbol")].Id,
                TargetSymbolId = symbols[ReadIndex(reader, symbolCount, "target symbol")].Id,
                Kind = (SemanticRelationshipKind)reader.ReadByte(),
            });
        }

        if (requireEnd && stream.Position != stream.Length)
        {
            throw new InvalidDataException("Semantic index has unexpected trailing data.");
        }

        return new SemanticIndex
        {
            RepositoryId = repositoryId,
            GenerationId = generationId,
            GitTree = gitTree,
            DirtyHash = dirtyHash,
            BaseCommit = baseCommit,
            IndexedAtUtc = indexedAtUtc,
            Documents = documents,
            Symbols = symbols,
            Occurrences = occurrences,
            Relationships = relationships,
        }.NormalizeAndValidate();
    }

    internal void WriteTo(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        NormalizeAndValidate().Write(writer);
    }

    internal SemanticIndex NormalizeForUse() => NormalizeAndValidate();

    private void Write(BinaryWriter writer)
    {
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(CurrentVersion);
        writer.Write(RepositoryId);
        writer.Write(GenerationId);
        writer.Write(GitTree);
        writer.Write(DirtyHash is not null);
        if (DirtyHash is not null)
        {
            writer.Write(DirtyHash);
        }

        writer.Write(BaseCommit);
        writer.Write(IndexedAtUtc.ToUniversalTime().Ticks);

        writer.Write(Documents.Count);
        foreach (var document in Documents)
        {
            writer.Write(document.RelPath);
            writer.Write(document.Hash);
        }

        writer.Write(Symbols.Count);
        foreach (var symbol in Symbols)
        {
            writer.Write(symbol.Id);
            writer.Write(symbol.DisplayName);
            writer.Write((byte)symbol.Kind);
            writer.Write(symbol.Signature);
            writer.Write(symbol.Documentation is not null);
            if (symbol.Documentation is not null)
            {
                writer.Write(symbol.Documentation);
            }
        }

        var documents = Documents
            .Select((document, index) => (document.RelPath, index))
            .ToDictionary(item => item.RelPath, item => item.index, StringComparer.Ordinal);
        var symbols = Symbols
            .Select((symbol, index) => (symbol.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);

        writer.Write(Occurrences.Count);
        foreach (var occurrence in Occurrences)
        {
            writer.Write(documents[occurrence.DocumentPath]);
            writer.Write(symbols[occurrence.SymbolId]);
            writer.Write(occurrence.Range.StartLine);
            writer.Write(occurrence.Range.StartCharacter);
            writer.Write(occurrence.Range.EndLine);
            writer.Write(occurrence.Range.EndCharacter);
            writer.Write((ushort)occurrence.Roles);
            writer.Write((byte)occurrence.Precision);
        }

        writer.Write(Relationships.Count);
        foreach (var relationship in Relationships)
        {
            writer.Write(symbols[relationship.SourceSymbolId]);
            writer.Write(symbols[relationship.TargetSymbolId]);
            writer.Write((byte)relationship.Kind);
        }
    }

    private SemanticIndex NormalizeAndValidate()
    {
        RequireText(RepositoryId, nameof(RepositoryId));
        RequireText(GenerationId, nameof(GenerationId));
        RequireText(GitTree, nameof(GitTree));

        var documents = Documents
            .Select(document => document with { RelPath = NormalizePath(document.RelPath) })
            .OrderBy(document => document.RelPath, StringComparer.Ordinal)
            .ToList();
        EnsureUnique(documents.Select(document => document.RelPath), "document path");
        foreach (var document in documents)
        {
            if (document.Hash is not { Length: 32 })
            {
                throw new InvalidDataException($"Document '{document.RelPath}' must have a 32-byte SHA-256 hash.");
            }
        }

        var symbols = Symbols.OrderBy(symbol => symbol.Id, StringComparer.Ordinal).ToList();
        EnsureUnique(symbols.Select(symbol => symbol.Id), "symbol ID");
        foreach (var symbol in symbols)
        {
            RequireText(symbol.Id, "symbol ID");
            RequireText(symbol.DisplayName, $"display name for '{symbol.Id}'");
            if (!Enum.IsDefined(symbol.Kind))
            {
                throw new InvalidDataException($"Symbol '{symbol.Id}' has an unknown kind.");
            }
        }

        var documentPaths = documents.Select(document => document.RelPath).ToHashSet(StringComparer.Ordinal);
        var symbolIds = symbols.Select(symbol => symbol.Id).ToHashSet(StringComparer.Ordinal);

        var occurrences = Occurrences
            .Select(occurrence => occurrence with { DocumentPath = NormalizePath(occurrence.DocumentPath) })
            .OrderBy(occurrence => occurrence.DocumentPath, StringComparer.Ordinal)
            .ThenBy(occurrence => occurrence.Range.StartLine)
            .ThenBy(occurrence => occurrence.Range.StartCharacter)
            .ThenBy(occurrence => occurrence.Range.EndLine)
            .ThenBy(occurrence => occurrence.Range.EndCharacter)
            .ThenBy(occurrence => occurrence.SymbolId, StringComparer.Ordinal)
            .ToList();
        foreach (var occurrence in occurrences)
        {
            if (!documentPaths.Contains(occurrence.DocumentPath))
            {
                throw new InvalidDataException($"Occurrence references unknown document '{occurrence.DocumentPath}'.");
            }

            if (!symbolIds.Contains(occurrence.SymbolId))
            {
                throw new InvalidDataException($"Occurrence references unknown symbol '{occurrence.SymbolId}'.");
            }

            ValidateRange(occurrence.Range);
            if (occurrence.Roles == SemanticOccurrenceRoles.None ||
                (occurrence.Roles & ~(SemanticOccurrenceRoles.Definition |
                                      SemanticOccurrenceRoles.Reference |
                                      SemanticOccurrenceRoles.Read |
                                      SemanticOccurrenceRoles.Write |
                                      SemanticOccurrenceRoles.Import)) != 0)
            {
                throw new InvalidDataException($"Occurrence for '{occurrence.SymbolId}' has invalid roles.");
            }

            if (occurrence.Precision == NavigationPrecision.Unknown || !Enum.IsDefined(occurrence.Precision))
            {
                throw new InvalidDataException($"Occurrence for '{occurrence.SymbolId}' has invalid precision.");
            }
        }

        var relationships = Relationships
            .OrderBy(relationship => relationship.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(relationship => relationship.Kind)
            .ThenBy(relationship => relationship.TargetSymbolId, StringComparer.Ordinal)
            .ToList();
        foreach (var relationship in relationships)
        {
            if (!symbolIds.Contains(relationship.SourceSymbolId) ||
                !symbolIds.Contains(relationship.TargetSymbolId))
            {
                throw new InvalidDataException("Semantic relationship references an unknown symbol.");
            }

            if (relationship.Kind == SemanticRelationshipKind.Unknown || !Enum.IsDefined(relationship.Kind))
            {
                throw new InvalidDataException("Semantic relationship has an unknown kind.");
            }
        }

        return this with
        {
            IndexedAtUtc = IndexedAtUtc.ToUniversalTime(),
            Documents = documents,
            Symbols = symbols,
            Occurrences = occurrences,
            Relationships = relationships,
        };
    }

    private static string NormalizePath(string path)
    {
        RequireText(path, "document path");
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Document path '{path}' is not a normalized repository-relative path.");
        }

        return normalized;
    }

    private static void ValidateRange(SourceRange range)
    {
        if (range.StartLine < 0 || range.StartCharacter < 0 ||
            range.EndLine < 0 || range.EndCharacter < 0 ||
            range.EndLine < range.StartLine ||
            (range.EndLine == range.StartLine && range.EndCharacter < range.StartCharacter))
        {
            throw new InvalidDataException($"Invalid semantic source range '{range}'.");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new InvalidDataException($"Duplicate {description} '{value}'.");
            }
        }
    }

    private static void RequireText(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Semantic index {description} is required.");
        }
    }

    private static int ReadCount(BinaryReader reader, string description)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > MaximumEntries)
        {
            throw new InvalidDataException($"Semantic index {description} count {count} is invalid.");
        }

        return count;
    }

    private static int ReadIndex(BinaryReader reader, int count, string description)
    {
        var index = reader.ReadInt32();
        if ((uint)index >= (uint)count)
        {
            throw new InvalidDataException($"Semantic index {description} index {index} is out of range.");
        }

        return index;
    }

    private static byte[] ReadHash(BinaryReader reader)
    {
        var hash = reader.ReadBytes(32);
        if (hash.Length != 32)
        {
            throw new InvalidDataException("Semantic index ended inside a document hash.");
        }

        return hash;
    }
}
