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
