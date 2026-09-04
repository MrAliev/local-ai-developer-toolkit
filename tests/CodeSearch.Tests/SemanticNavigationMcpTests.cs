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
            line: 5,
            column: 13);

        Assert.StartsWith("Definitions: 1", response, StringComparison.Ordinal);
        Assert.Contains("<untrusted-content", response, StringComparison.Ordinal);
        Assert.Contains("origin=\"go_to_definition:Src/A.cs\"", response, StringComparison.Ordinal);
        Assert.Contains("Src/A.cs:3:17-3:19", response, StringComparison.Ordinal);
        Assert.Contains(MethodId, response, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_references_can_exclude_the_definition()
    {
        var gateway = Gateway();

        var response = CodeSearchTools.FindReferences(
            gateway,
            "Src/Use.cs",
            line: 5,
            column: 13,
            includeDefinition: false);

        Assert.StartsWith("References: 1", response, StringComparison.Ordinal);
        Assert.Contains("Src/Use.cs:5:11-5:15", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Src/A.cs:3:17-3:19", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_implementations_wraps_precise_relationship_results()
    {
        var response = CodeSearchTools.FindImplementations(
            Gateway(),
            "Src/A.cs",
            line: 3,
            column: 18);

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
            line: 4,
            column: 18,
            direction: "outgoing",
            kind: "implementation");

        Assert.StartsWith("Relationships: 1", response, StringComparison.Ordinal);
        Assert.Contains("relationship: Implementation", response, StringComparison.Ordinal);
        Assert.Contains("direction: Outgoing", response, StringComparison.Ordinal);
        Assert.Contains("Src/A.cs:3:17-3:19", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_readiness_errors_remain_trusted_and_unwrapped()
    {
        var gateway = new SemanticNavigationGateway(
            _ => throw new SemanticNavigationNotReadyException("semantic.sidx is absent"));

        var response = CodeSearchTools.GoToDefinition(
            gateway,
            "Src/Use.cs",
            line: 5,
            column: 13);

        Assert.StartsWith("semantic_navigation_not_ready:", response, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The promise the tool descriptions make, asserted rather than described: the start line a
    /// hit is printed with is the line that navigates, and no column has to be worked out.
    ///
    /// `search_code` prints one-based lines and the navigation tools took zero-based ones, with
    /// nothing converting between them. Pasting a printed line landed one line into the body —
    /// a refusal when nothing was there, and a confident `Precise` answer about the wrong symbol
    /// when an identifier happened to be.
    /// </summary>
    [Fact]
    public void A_hit_navigates_at_the_line_it_is_printed_with()
    {
        // What search_code would print for the declaration this fixture puts at zero-based 2.
        const int printedStartLine = 3;

        var response = CodeSearchTools.GoToDefinition(
            Gateway(),
            "Src/A.cs",
            line: printedStartLine);

        Assert.StartsWith("Definitions: 1", response, StringComparison.Ordinal);
        Assert.Contains("Src/A.cs:3:17-3:19", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero can only be a position somebody counted from zero, so it is refused by name rather
    /// than passed to a core that would answer about the line above.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void A_position_counted_from_zero_is_refused_by_name(int line, int column)
    {
        var response = CodeSearchTools.GoToDefinition(Gateway(), "Src/A.cs", line, column);

        Assert.StartsWith("invalid_position:", response, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path search_code would never have printed is refused by name, the way a zero position
    /// is. Passed on, it came back as the core's ArgumentException — English, with a framework
    /// parameter suffix glued on — inside an answer that otherwise follows the reader.
    /// </summary>
    [Theory]
    [InlineData(@"R:\LocalAi\Src\A.cs")]
    [InlineData("/Src/A.cs")]
    [InlineData("../Src/A.cs")]
    [InlineData("Src/./A.cs")]
    public void A_path_that_is_not_repository_relative_is_refused_by_name(string path)
    {
        var response = CodeSearchTools.GoToDefinition(Gateway(), path, line: 3);

        Assert.StartsWith("invalid_path:", response, StringComparison.Ordinal);
        Assert.Contains(path, response, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", response, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_navigation_tool_refuses_such_a_path_with_the_same_words()
    {
        const string path = @"R:\LocalAi\Src\A.cs";
        var gateway = Gateway();
        var expected = CodeSearchTools.GoToDefinition(gateway, path, line: 3);

        Assert.All(
            new[]
            {
                CodeSearchTools.FindReferences(gateway, path, line: 5),
                CodeSearchTools.FindImplementations(gateway, path, line: 3),
                CodeSearchTools.FindRelationships(gateway, path, line: 4),
            },
            answer => Assert.Equal(expected, answer));
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
