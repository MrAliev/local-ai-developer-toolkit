using CodeSearch.Core.Semantics;
using CodeSearch.Mcp;

namespace CodeSearch.Tests;

public class SemanticNavigationMcpTests
{
    [Fact]
    public void Go_to_definition_wraps_each_source_derived_location()
    {
        var gateway = Gateway();

        var response = CodeSearchTools.GoToDefinition(
            gateway,
            "Src/Use.cs",
            line: 4,
            utf16Column: 12);

        Assert.StartsWith("Definitions: 1", response, StringComparison.Ordinal);
        Assert.Contains("<untrusted-content", response, StringComparison.Ordinal);
        Assert.Contains("origin=\"go_to_definition:Src/A.cs\"", response, StringComparison.Ordinal);
        Assert.Contains("Src/A.cs:2:16-2:18", response, StringComparison.Ordinal);
        Assert.Contains(MethodId, response, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_references_can_exclude_the_definition()
    {
        var gateway = Gateway();

        var response = CodeSearchTools.FindReferences(
            gateway,
            "Src/Use.cs",
            line: 4,
            utf16Column: 12,
            includeDefinition: false);

        Assert.StartsWith("References: 1", response, StringComparison.Ordinal);
        Assert.Contains("Src/Use.cs:4:10-4:14", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Src/A.cs:2:16-2:18", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_implementations_wraps_precise_relationship_results()
    {
        var response = CodeSearchTools.FindImplementations(
            Gateway(),
            "Src/A.cs",
            line: 2,
            utf16Column: 17);

        Assert.StartsWith("Implementations: 1", response, StringComparison.Ordinal);
        Assert.Contains(
            "origin=\"find_implementations:Src/Impl.cs\"",
            response,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Find_relationships_reports_kind_and_direction()
    {
        var response = CodeSearchTools.FindRelationships(
            Gateway(),
            "Src/Impl.cs",
            line: 3,
            utf16Column: 17,
            direction: "outgoing",
            kind: "implementation");

        Assert.StartsWith("Relationships: 1", response, StringComparison.Ordinal);
        Assert.Contains("relationship: Implementation", response, StringComparison.Ordinal);
        Assert.Contains("direction: Outgoing", response, StringComparison.Ordinal);
        Assert.Contains("Src/A.cs:2:16-2:18", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_readiness_errors_remain_trusted_and_unwrapped()
    {
        var gateway = new SemanticNavigationGateway(
            _ => throw new SemanticNavigationNotReadyException("semantic.sidx is absent"));

        var response = CodeSearchTools.GoToDefinition(
            gateway,
            "Src/Use.cs",
            line: 4,
            utf16Column: 12);

        Assert.StartsWith("semantic_navigation_not_ready:", response, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", response, StringComparison.Ordinal);
    }

    private const string MethodId = "scip-dotnet pkg MyApp 1.0.0 A#Go().";
    private const string ImplementationId = "scip-dotnet pkg MyApp 1.0.0 Impl#Go().";

    private static SemanticNavigationGateway Gateway()
    {
        var index = new SemanticIndex
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            BaseCommit = "commit",
            IndexedAtUtc = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                Document("Src/A.cs", 1),
                Document("Src/Impl.cs", 3),
                Document("Src/Use.cs", 2),
            ],
            Symbols =
            [
                new SemanticSymbol
                {
                    Id = MethodId,
                    DisplayName = "Go",
                    Kind = SemanticSymbolKind.Method,
                    Signature = "void A.Go()",
                },
                new SemanticSymbol
                {
                    Id = ImplementationId,
                    DisplayName = "Go",
                    Kind = SemanticSymbolKind.Method,
                    Signature = "void Impl.Go()",
                },
            ],
            Occurrences =
            [
                Occurrence("Src/A.cs", new SourceRange(2, 16, 2, 18),
                    SemanticOccurrenceRoles.Definition),
                Occurrence("Src/Use.cs", new SourceRange(4, 10, 4, 14),
                    SemanticOccurrenceRoles.Reference | SemanticOccurrenceRoles.Read),
                new SemanticOccurrence
                {
                    DocumentPath = "Src/Impl.cs",
                    Range = new SourceRange(3, 16, 3, 18),
                    SymbolId = ImplementationId,
                    Roles = SemanticOccurrenceRoles.Definition,
                    Precision = NavigationPrecision.Precise,
                },
            ],
            Relationships =
            [
                new SemanticRelationship
                {
                    SourceSymbolId = ImplementationId,
                    TargetSymbolId = MethodId,
                    Kind = SemanticRelationshipKind.Implementation,
                },
            ],
        };
        var snapshot = new SemanticSnapshotIdentity("repository", "generation", "tree", null);
        return new SemanticNavigationGateway(
            _ => new SemanticNavigationContext(new SemanticNavigationService(index), snapshot));
    }

    private static SemanticDocument Document(string path, byte hashByte) =>
        new()
        {
            RelPath = path,
            Hash = Enumerable.Repeat(hashByte, 32).ToArray(),
        };

    private static SemanticOccurrence Occurrence(
        string path,
        SourceRange range,
        SemanticOccurrenceRoles roles) =>
        new()
        {
            DocumentPath = path,
            Range = range,
            SymbolId = MethodId,
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };
}
