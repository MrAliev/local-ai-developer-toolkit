using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public class SemanticNavigationServiceTests
{
    [Fact]
    public void ResolvesTheNarrowestContainingOccurrence()
    {
        var service = new SemanticNavigationService(Index());

        var occurrence = service.ResolveOccurrence("Src/Use.cs", 4, 12, Snapshot());

        Assert.NotNull(occurrence);
        Assert.Equal(MethodId, occurrence.SymbolId);
        Assert.Equal(new SourceRange(4, 10, 4, 14), occurrence.Range);
    }

    [Fact]
    public void GoesFromAReferenceToTheDefinition()
    {
        var service = new SemanticNavigationService(Index());

        var definitions = service.GoToDefinition("Src/Use.cs", 4, 12, Snapshot());

        var definition = Assert.Single(definitions);
        Assert.Equal("Src/A.cs", definition.DocumentPath);
        Assert.Equal(new SourceRange(2, 16, 2, 18), definition.Range);
        Assert.True(definition.Roles.HasFlag(SemanticOccurrenceRoles.Definition));
    }

    [Fact]
    public void FindsReferencesWithAnOptionalDefinitionInStableOrder()
    {
        var service = new SemanticNavigationService(Index());

        var references = service.FindReferences(
            "Src/Use.cs", 4, 12, includeDefinition: false, Snapshot());
        var all = service.FindReferences(
            "Src/Use.cs", 4, 12, includeDefinition: true, Snapshot());

        Assert.Equal(["Src/Use.cs", "Src/Б.cs"], references.Select(location => location.DocumentPath));
        Assert.Equal(["Src/A.cs", "Src/Use.cs", "Src/Б.cs"], all.Select(location => location.DocumentPath));
    }

    [Fact]
    public void FindsImplementationsOverridesAndDerivedTypesInStableOrder()
    {
        var service = new SemanticNavigationService(Index());

        var implementations = service.FindImplementations(
            "Src/A.cs", 2, 17, Snapshot());

        Assert.Equal(
            ["Src/Derived.cs", "Src/Implementation.cs"],
            implementations.Select(location => location.DocumentPath));
        Assert.All(implementations, location =>
            Assert.Equal(NavigationPrecision.Precise, location.Precision));
    }

    [Fact]
    public void QueriesIncomingAndOutgoingRelationshipsByKind()
    {
        var service = new SemanticNavigationService(Index());

        var incoming = service.FindRelationships(
            "Src/A.cs", 2, 17,
            SemanticRelationshipDirection.Incoming,
            SemanticRelationshipKind.Override,
            Snapshot());
        var outgoing = service.FindRelationships(
            "Src/Implementation.cs", 5, 21,
            SemanticRelationshipDirection.Outgoing,
            kind: null,
            Snapshot());

        Assert.Equal("Src/Derived.cs", Assert.Single(incoming).Location.DocumentPath);
        var relatedBase = Assert.Single(outgoing);
        Assert.Equal("Src/A.cs", relatedBase.Location.DocumentPath);
        Assert.Equal(SemanticRelationshipKind.Implementation, relatedBase.Kind);
    }

    [Fact]
    public void ResolvesTheDeclarationOnTheLineWhenThePositionNamesNothing()
    {
        // What a search result gives a caller: a path and a line. The column of the identifier
        // inside that line is not in the hit, so the natural call is column zero — and before
        // this, that landed on the indent, resolved to nothing, and fell through to the text
        // heuristic.
        var service = new SemanticNavigationService(Index());

        var occurrence = service.ResolveOccurrence("Src/A.cs", 2, 0, Snapshot());

        Assert.NotNull(occurrence);
        Assert.Equal(MethodId, occurrence.SymbolId);
        Assert.Equal(new SourceRange(2, 16, 2, 18), occurrence.Range);
    }

    [Fact]
    public void Navigates_from_the_first_line_of_a_declaration()
    {
        var service = new SemanticNavigationService(Index());

        var references = service.FindReferences(
            "Src/A.cs", 2, 0, includeDefinition: false, Snapshot());

        Assert.Equal(["Src/Use.cs", "Src/Б.cs"], references.Select(location => location.DocumentPath));
    }

    [Fact]
    public void Declines_a_line_that_declares_more_than_one_thing()
    {
        // `const a = f(), b = g()`. There is no single answer, so the caller gets the same
        // nothing it got before rather than whichever declaration happened to be listed first.
        var service = new SemanticNavigationService(IndexWithTwoDeclarationsOnOneLine());

        Assert.Null(service.ResolveOccurrence("Src/Pair.cs", 7, 0, Snapshot()));
    }

    /// <summary>
    /// `private static bool IsRunAlive(string journalPath)` — a single-line signature declares
    /// the method and its parameter, which in C# is most method lines there are. Under an
    /// exactly-one rule, column 0 on exactly the hits the shortcut was written for refused to
    /// navigate. The parameter's token lies inside the method's enclosing range, so the method
    /// is the outermost declaration by containment.
    /// </summary>
    [Fact]
    public void Resolves_a_signature_line_to_the_method_rather_than_its_parameter()
    {
        var service = new SemanticNavigationService(IndexWithSignatureLine());

        var occurrence = service.ResolveOccurrence("Src/Signature.cs", 9, 0, Snapshot());

        Assert.NotNull(occurrence);
        Assert.Equal(SignatureMethodId, occurrence.SymbolId);
    }

    /// <summary>
    /// Containment must be proven, never assumed: two declarations whose bodies sit side by
    /// side contain each other's tokens in neither direction, and a declaration without an
    /// enclosing range cannot contain anything at all. Both stay refused.
    /// </summary>
    [Fact]
    public void Declines_sibling_declarations_even_when_each_has_its_own_body()
    {
        var service = new SemanticNavigationService(IndexWithSiblingBodiesOnOneLine());

        Assert.Null(service.ResolveOccurrence("Src/Siblings.cs", 11, 0, Snapshot()));
    }

    [Fact]
    public void Does_not_resolve_a_line_that_only_references_something()
    {
        // Line 4 of Use.cs holds references and no declaration. Resolving column zero there to
        // one of them would answer about a symbol the caller never pointed at.
        var service = new SemanticNavigationService(Index());

        Assert.Null(service.ResolveOccurrence("Src/Use.cs", 4, 0, Snapshot()));
    }

    [Fact]
    public void RejectsAQueryForAnotherSnapshot()
    {
        var service = new SemanticNavigationService(Index());
        var wrong = Snapshot() with { GitTree = "another-tree" };

        Assert.Throws<SemanticSnapshotMismatchException>(
            () => service.GoToDefinition("Src/Use.cs", 4, 12, wrong));
    }

    private const string TypeId = "scip-dotnet pkg MyApp 1.0.0 A#";
    private const string MethodId = "scip-dotnet pkg MyApp 1.0.0 A#Go().";
    private const string DerivedMethodId = "scip-dotnet pkg MyApp 1.0.0 Derived#Go().";
    private const string ImplementationMethodId = "scip-dotnet pkg MyApp 1.0.0 Implementation#Go().";

    private static SemanticSnapshotIdentity Snapshot() =>
        new("repository", "generation", "tree", "dirty");

    private static SemanticIndex Index() =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = "dirty",
            BaseCommit = "commit",
            IndexedAtUtc = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                Document("Src/A.cs", 1),
                Document("Src/Derived.cs", 4),
                Document("Src/Implementation.cs", 5),
                Document("Src/Use.cs", 2),
                Document("Src/Б.cs", 3),
            ],
            Symbols =
            [
                new SemanticSymbol
                {
                    Id = TypeId,
                    DisplayName = "A",
                    Kind = SemanticSymbolKind.Type,
                    Signature = "class A",
                },
                new SemanticSymbol
                {
                    Id = MethodId,
                    DisplayName = "Go",
                    Kind = SemanticSymbolKind.Method,
                    Signature = "void A.Go()",
                },
                new SemanticSymbol
                {
                    Id = DerivedMethodId,
                    DisplayName = "Go",
                    Kind = SemanticSymbolKind.Method,
                    Signature = "override void Derived.Go()",
                },
                new SemanticSymbol
                {
                    Id = ImplementationMethodId,
                    DisplayName = "Go",
                    Kind = SemanticSymbolKind.Method,
                    Signature = "void Implementation.Go()",
                },
            ],
            Occurrences =
            [
                Occurrence("Src/A.cs", new SourceRange(2, 16, 2, 18), MethodId,
                    SemanticOccurrenceRoles.Definition),
                Occurrence("Src/Derived.cs", new SourceRange(3, 20, 3, 22), DerivedMethodId,
                    SemanticOccurrenceRoles.Definition),
                Occurrence("Src/Implementation.cs", new SourceRange(5, 20, 5, 22), ImplementationMethodId,
                    SemanticOccurrenceRoles.Definition),
                Occurrence("Src/Use.cs", new SourceRange(4, 4, 4, 20), TypeId,
                    SemanticOccurrenceRoles.Reference),
                Occurrence("Src/Use.cs", new SourceRange(4, 10, 4, 14), MethodId,
                    SemanticOccurrenceRoles.Reference | SemanticOccurrenceRoles.Read),
                Occurrence("Src/Б.cs", new SourceRange(8, 2, 8, 4), MethodId,
                    SemanticOccurrenceRoles.Reference | SemanticOccurrenceRoles.Read),
            ],
            Relationships =
            [
                new SemanticRelationship
                {
                    SourceSymbolId = DerivedMethodId,
                    TargetSymbolId = MethodId,
                    Kind = SemanticRelationshipKind.Override,
                },
                new SemanticRelationship
                {
                    SourceSymbolId = ImplementationMethodId,
                    TargetSymbolId = MethodId,
                    Kind = SemanticRelationshipKind.Implementation,
                },
            ],
        };

    /// <summary>
    /// One line, two declarations: <c>const a = f(), b = g()</c> as the indexer reports it.
    /// </summary>
    private static SemanticIndex IndexWithTwoDeclarationsOnOneLine() =>
        Index() with
        {
            Documents = [.. Index().Documents, Document("Src/Pair.cs", 6)],
            Occurrences =
            [
                .. Index().Occurrences,
                Occurrence("Src/Pair.cs", new SourceRange(7, 6, 7, 7), TypeId,
                    SemanticOccurrenceRoles.Definition),
                Occurrence("Src/Pair.cs", new SourceRange(7, 15, 7, 16), MethodId,
                    SemanticOccurrenceRoles.Definition),
            ],
        };

    private const string SignatureMethodId = "scip-dotnet pkg MyApp 1.0.0 Journal#IsRunAlive().";
    private const string SignatureParameterId =
        "scip-dotnet pkg MyApp 1.0.0 Journal#IsRunAlive().(journalPath)";

    /// <summary>
    /// A single-line C# method signature: the method's identifier token on the line, its
    /// enclosing range spanning the whole body, and the parameter's token inside it.
    /// </summary>
    private static SemanticIndex IndexWithSignatureLine() =>
        Index() with
        {
            Documents = [.. Index().Documents, Document("Src/Signature.cs", 8)],
            Occurrences =
            [
                .. Index().Occurrences,
                Occurrence("Src/Signature.cs", new SourceRange(9, 24, 9, 34), SignatureMethodId,
                    SemanticOccurrenceRoles.Definition) with
                {
                    EnclosingRange = new SourceRange(9, 4, 24, 5),
                },
                Occurrence(
                    "Src/Signature.cs",
                    new SourceRange(9, 42, 9, 53),
                    SignatureParameterId,
                    SemanticOccurrenceRoles.Definition),
            ],
        };

    /// <summary>
    /// Two declarations on one line, each with a body of its own beside the other's: neither
    /// enclosing range contains the other declaration's token.
    /// </summary>
    private static SemanticIndex IndexWithSiblingBodiesOnOneLine() =>
        Index() with
        {
            Documents = [.. Index().Documents, Document("Src/Siblings.cs", 10)],
            Occurrences =
            [
                .. Index().Occurrences,
                Occurrence("Src/Siblings.cs", new SourceRange(11, 5, 11, 6), TypeId,
                    SemanticOccurrenceRoles.Definition) with
                {
                    EnclosingRange = new SourceRange(11, 0, 11, 20),
                },
                Occurrence("Src/Siblings.cs", new SourceRange(11, 30, 11, 31), MethodId,
                    SemanticOccurrenceRoles.Definition) with
                {
                    EnclosingRange = new SourceRange(11, 25, 11, 45),
                },
            ],
        };

    private static SemanticDocument Document(string path, byte hashByte) =>
        new()
        {
            RelPath = path,
            Hash = Enumerable.Repeat(hashByte, 32).ToArray(),
        };

    private static SemanticOccurrence Occurrence(
        string path,
        SourceRange range,
        string symbol,
        SemanticOccurrenceRoles roles) =>
        new()
        {
            DocumentPath = path,
            Range = range,
            SymbolId = symbol,
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };
}
