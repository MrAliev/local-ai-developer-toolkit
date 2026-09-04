using CodeSearch.Core.Resources;

namespace CodeSearch.Core.Semantics;

public sealed record SemanticSnapshotIdentity(
    string RepositoryId,
    string GenerationId,
    string GitTree,
    string? DirtyHash);

public sealed record SemanticLocation(
    string DocumentPath,
    SourceRange Range,
    string SymbolId,
    SemanticOccurrenceRoles Roles,
    NavigationPrecision Precision);

public sealed record SemanticRelatedLocation(
    SemanticLocation Location,
    SemanticRelationshipKind Kind,
    SemanticRelationshipDirection Direction);

public sealed class SemanticSnapshotMismatchException(string message) : InvalidOperationException(message);

/// <summary>Exact position, definition, and reference queries over one immutable semantic snapshot.</summary>
public sealed class SemanticNavigationService
{
    private readonly SemanticSnapshotIdentity _identity;
    private readonly Dictionary<string, List<SemanticOccurrence>> _byDocument;
    private readonly Dictionary<string, List<SemanticOccurrence>> _definitions;
    private readonly Dictionary<string, List<SemanticOccurrence>> _references;
    private readonly Dictionary<string, List<SemanticRelationship>> _relationshipsByTarget;
    private readonly Dictionary<string, List<SemanticRelationship>> _relationshipsBySource;

    public SemanticNavigationService(SemanticIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _identity = new SemanticSnapshotIdentity(
            index.RepositoryId,
            index.GenerationId,
            index.GitTree,
            index.DirtyHash);

        _byDocument = index.Occurrences
            .GroupBy(occurrence => NormalizePath(occurrence.DocumentPath), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.Ordinal);
        _definitions = GroupBySymbol(
            index.Occurrences.Where(occurrence =>
                occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition)));
        _references = GroupBySymbol(
            index.Occurrences.Where(occurrence =>
                occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Reference)));
        _relationshipsByTarget = index.Relationships
            .GroupBy(relationship => relationship.TargetSymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.Ordinal);
        _relationshipsBySource = index.Relationships
            .GroupBy(relationship => relationship.SourceSymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.Ordinal);
    }

    public SemanticLocation? ResolveOccurrence(
        string documentPath,
        int line,
        int utf16Column,
        SemanticSnapshotIdentity snapshot)
    {
        RequireSnapshot(snapshot);
        return Resolve(documentPath, line, utf16Column) is { } occurrence
            ? ToLocation(occurrence)
            : null;
    }

    public IReadOnlyList<SemanticLocation> GoToDefinition(
        string documentPath,
        int line,
        int utf16Column,
        SemanticSnapshotIdentity snapshot)
    {
        RequireSnapshot(snapshot);
        var occurrence = Resolve(documentPath, line, utf16Column);
        return occurrence is null || !_definitions.TryGetValue(occurrence.SymbolId, out var definitions)
            ? []
            : OrderedLocations(definitions);
    }

    public IReadOnlyList<SemanticLocation> FindReferences(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        SemanticSnapshotIdentity snapshot)
    {
        RequireSnapshot(snapshot);
        var occurrence = Resolve(documentPath, line, utf16Column);
        if (occurrence is null)
        {
            return [];
        }

        var matches = _references.TryGetValue(occurrence.SymbolId, out var references)
            ? references.AsEnumerable()
            : [];
        if (includeDefinition && _definitions.TryGetValue(occurrence.SymbolId, out var definitions))
        {
            matches = matches.Concat(definitions);
        }

        return OrderedLocations(matches);
    }

    public IReadOnlyList<SemanticLocation> FindImplementations(
        string documentPath,
        int line,
        int utf16Column,
        SemanticSnapshotIdentity snapshot)
    {
        RequireSnapshot(snapshot);
        var occurrence = Resolve(documentPath, line, utf16Column);
        if (occurrence is null ||
            !_relationshipsByTarget.TryGetValue(occurrence.SymbolId, out var relationships))
        {
            return [];
        }

        var definitions = relationships
            .Where(relationship => relationship.Kind is
                SemanticRelationshipKind.Implementation or
                SemanticRelationshipKind.Override or
                SemanticRelationshipKind.TypeDefinition)
            .SelectMany(relationship =>
                _definitions.TryGetValue(relationship.SourceSymbolId, out var matches)
                    ? matches
                    : [])
            .Distinct();
        return OrderedLocations(definitions);
    }

    public IReadOnlyList<SemanticRelatedLocation> FindRelationships(
        string documentPath,
        int line,
        int utf16Column,
        SemanticRelationshipDirection direction,
        SemanticRelationshipKind? kind,
        SemanticSnapshotIdentity snapshot)
    {
        RequireSnapshot(snapshot);
        if (!Enum.IsDefined(direction) ||
            kind is SemanticRelationshipKind.Unknown ||
            kind is not null && !Enum.IsDefined(kind.Value))
        {
            throw new ArgumentOutOfRangeException(
                kind is not null ? nameof(kind) : nameof(direction));
        }

        var occurrence = Resolve(documentPath, line, utf16Column);
        if (occurrence is null)
        {
            return [];
        }

        var index = direction == SemanticRelationshipDirection.Incoming
            ? _relationshipsByTarget
            : _relationshipsBySource;
        if (!index.TryGetValue(occurrence.SymbolId, out var relationships))
        {
            return [];
        }

        return relationships
            .Where(relationship => kind is null || relationship.Kind == kind)
            .SelectMany(relationship =>
            {
                var relatedId = direction == SemanticRelationshipDirection.Incoming
                    ? relationship.SourceSymbolId
                    : relationship.TargetSymbolId;
                return _definitions.TryGetValue(relatedId, out var definitions)
                    ? definitions.Select(definition => new SemanticRelatedLocation(
                        ToLocation(definition), relationship.Kind, direction))
                    : [];
            })
            .Distinct()
            .OrderBy(result => result.Location.DocumentPath, StringComparer.Ordinal)
            .ThenBy(result => result.Location.Range.StartLine)
            .ThenBy(result => result.Location.Range.StartCharacter)
            .ThenBy(result => result.Kind)
            .ToList();
    }

    private SemanticOccurrence? Resolve(string documentPath, int line, int utf16Column)
    {
        if (line < 0 || utf16Column < 0)
        {
            throw new ArgumentOutOfRangeException(
                line < 0 ? nameof(line) : nameof(utf16Column),
                "Semantic positions are zero-based and cannot be negative.");
        }

        var path = NormalizePath(documentPath);
        if (!_byDocument.TryGetValue(path, out var occurrences))
        {
            return null;
        }

        var exact = occurrences
            .Where(occurrence => Contains(occurrence.Range, line, utf16Column))
            .OrderByDescending(occurrence => occurrence.Range.StartLine)
            .ThenByDescending(occurrence => occurrence.Range.StartCharacter)
            .ThenBy(occurrence => occurrence.Range.EndLine)
            .ThenBy(occurrence => occurrence.Range.EndCharacter)
            .FirstOrDefault();

        return exact ?? SoleDeclarationOn(occurrences, line);
    }

    /// <summary>
    /// The one thing a line declares, for a position on that line that names nothing itself.
    /// </summary>
    /// <remarks>
    /// This is what makes a search result navigable. A hit reports a path and a line range; the
    /// column of the identifier inside that line is nowhere in it, so the obvious call is column
    /// zero — which lands on <c>export</c>, or on an indent, resolves to no symbol, and falls
    /// through to the bounded text heuristic. Asking for references to a component then returns
    /// matches for the word <c>export</c> in a swagger file, correctly labelled as degraded and
    /// completely useless.
    ///
    /// A line that declares exactly one thing has exactly one answer, and giving it is not a
    /// guess. A line whose declarations all nest inside one of them has one answer too: a
    /// single-line method signature declares the method and its parameters, which is the
    /// normal C# shape — under an exactly-one rule, the very hits the shortcut was built for
    /// refused to navigate. The parameters live inside the method's enclosing range, so the
    /// method is the outermost declaration by containment, not by guesswork. What stays
    /// unresolved is a line of genuine siblings — <c>const a = f(), b = g()</c> — where
    /// neither contains the other, and the caller gets the same degradation notice rather
    /// than a coin flip. Only declarations count: resolving a position to a reference that
    /// merely happens to share the line would answer about a different symbol entirely.
    /// </remarks>
    private static SemanticOccurrence? SoleDeclarationOn(
        List<SemanticOccurrence> occurrences,
        int line)
    {
        var declarations = new List<SemanticOccurrence>();
        foreach (var occurrence in occurrences)
        {
            if (occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) &&
                occurrence.Range.StartLine == line &&
                !declarations.Any(existing => string.Equals(
                    existing.SymbolId,
                    occurrence.SymbolId,
                    StringComparison.Ordinal)))
            {
                declarations.Add(occurrence);
            }
        }

        return declarations.Count switch
        {
            0 => null,
            1 => declarations[0],
            _ => OutermostDeclaration(declarations),
        };
    }

    /// <summary>
    /// The declaration whose enclosing range contains every other declaration on the line, or
    /// null when there is no such single container. A declaration without an enclosing range
    /// cannot contain anything — the indexers do not report one for every definition, and a
    /// missing span must degrade to the old refusal rather than to a guess.
    /// </summary>
    private static SemanticOccurrence? OutermostDeclaration(
        List<SemanticOccurrence> declarations)
    {
        SemanticOccurrence? outermost = null;
        foreach (var candidate in declarations)
        {
            if (candidate.EnclosingRange is not { } enclosing ||
                !declarations.All(other =>
                    ReferenceEquals(other, candidate) || Contains(enclosing, other.Range)))
            {
                continue;
            }

            if (outermost is not null)
            {
                return null;
            }

            outermost = candidate;
        }

        return outermost;
    }

    private static bool Contains(SourceRange outer, SourceRange inner) =>
        (outer.StartLine < inner.StartLine ||
         (outer.StartLine == inner.StartLine && outer.StartCharacter <= inner.StartCharacter)) &&
        (outer.EndLine > inner.EndLine ||
         (outer.EndLine == inner.EndLine && outer.EndCharacter >= inner.EndCharacter));

    private void RequireSnapshot(SemanticSnapshotIdentity snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(_identity.RepositoryId, snapshot.RepositoryId, StringComparison.Ordinal) ||
            !string.Equals(_identity.GenerationId, snapshot.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(_identity.GitTree, snapshot.GitTree, StringComparison.Ordinal) ||
            !string.Equals(_identity.DirtyHash, snapshot.DirtyHash, StringComparison.Ordinal))
        {
            throw new SemanticSnapshotMismatchException(IndexText.SnapshotMismatch);
        }
    }

    private static Dictionary<string, List<SemanticOccurrence>> GroupBySymbol(
        IEnumerable<SemanticOccurrence> occurrences) =>
        occurrences
            .GroupBy(occurrence => occurrence.SymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.Ordinal);

    private static IReadOnlyList<SemanticLocation> OrderedLocations(
        IEnumerable<SemanticOccurrence> occurrences) =>
        occurrences
            .OrderBy(occurrence => NormalizePath(occurrence.DocumentPath), StringComparer.Ordinal)
            .ThenBy(occurrence => occurrence.Range.StartLine)
            .ThenBy(occurrence => occurrence.Range.StartCharacter)
            .ThenBy(occurrence => occurrence.Range.EndLine)
            .ThenBy(occurrence => occurrence.Range.EndCharacter)
            .Select(ToLocation)
            .ToList();

    private static SemanticLocation ToLocation(SemanticOccurrence occurrence) =>
        new(
            NormalizePath(occurrence.DocumentPath),
            occurrence.Range,
            occurrence.SymbolId,
            occurrence.Roles,
            occurrence.Precision);

    private static bool Contains(SourceRange range, int line, int character)
    {
        var afterStart = line > range.StartLine ||
                         (line == range.StartLine && character >= range.StartCharacter);
        var beforeEnd = line < range.EndLine ||
                        (line == range.EndLine && character < range.EndCharacter);
        return afterStart && beforeEnd;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!SemanticDocumentPath.IsRepositoryRelative(path))
        {
            throw new ArgumentException("Semantic document path must be repository-relative.", nameof(path));
        }

        return path.Replace('\\', '/');
    }
}
