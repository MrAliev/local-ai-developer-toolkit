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

    /// <summary>
    /// The span of the whole definition this occurrence names, when the source of the occurrence
    /// reports one. Null everywhere else, including on every reference.
    /// </summary>
    /// <remarks>
    /// <see cref="Range"/> is the name. Navigation only ever needed that, which is why this was
    /// not read until now: chunking by symbol needs the body, and the body is a different span.
    ///
    /// Measured before relying on it (issue #87): `scip-python` reports it for every definition
    /// that has a body, nested functions and decorated definitions included, and starts a
    /// decorated definition at its decorator. `scip-typescript` reports it for every definition
    /// it gives a global symbol, and for neither of two kinds it does not — a definition declared
    /// inside a function body, and one whose initialiser is a call, which is what
    /// `export const X = memo(() =&gt; …)` is. Consumers have to survive its absence.
    /// </remarks>
    public SourceRange? EnclosingRange { get; init; }
}

public sealed record SemanticRelationship
{
    public required string SourceSymbolId { get; init; }
    public required string TargetSymbolId { get; init; }
    public required SemanticRelationshipKind Kind { get; init; }
}
