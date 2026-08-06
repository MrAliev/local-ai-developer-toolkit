namespace CodeSearch.Core.Semantics;

public enum SemanticSymbolKind : byte
{
    Unknown = 0,
    Namespace = 1,
    Type = 2,
    Method = 3,
    Property = 4,
    Field = 5,
    Event = 6,
    Parameter = 7,
    Local = 8,
    Resource = 9,
}

[Flags]
public enum SemanticOccurrenceRoles : ushort
{
    None = 0,
    Definition = 1 << 0,
    Reference = 1 << 1,
    Read = 1 << 2,
    Write = 1 << 3,
    Import = 1 << 4,
}

public enum NavigationPrecision : byte
{
    Unknown = 0,
    Precise = 1,
    Inferred = 2,
    Heuristic = 3,
}

public enum SemanticRelationshipKind : byte
{
    Unknown = 0,
    Implementation = 1,
    Override = 2,
    TypeDefinition = 3,
}

public enum SemanticRelationshipDirection : byte
{
    Incoming = 1,
    Outgoing = 2,
}

/// <summary>Zero-based line and UTF-16 column range, end-exclusive.</summary>
public readonly record struct SourceRange(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);

public sealed record SemanticDocument
{
    public required string RelPath { get; init; }
    public required byte[] Hash { get; init; }
}

public sealed record SemanticSymbol
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required SemanticSymbolKind Kind { get; init; }
    public string Signature { get; init; } = string.Empty;
    public string? Documentation { get; init; }
}

public sealed record SemanticOccurrence
{
    public required string DocumentPath { get; init; }
    public required SourceRange Range { get; init; }
    public required string SymbolId { get; init; }
    public required SemanticOccurrenceRoles Roles { get; init; }
    public required NavigationPrecision Precision { get; init; }
}

public sealed record SemanticRelationship
{
    public required string SourceSymbolId { get; init; }
    public required string TargetSymbolId { get; init; }
    public required SemanticRelationshipKind Kind { get; init; }
}
