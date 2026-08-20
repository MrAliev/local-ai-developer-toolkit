using CodeSearch.Core.Chunking;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class SymbolDefinitionCatalogTests
{
    private const string Module = "scip-typescript npm app 1.0.0 src/`App.tsx`/";

    [Fact]
    public void Keeps_a_scip_declaration_that_reported_no_body()
    {
        var catalog = Catalog(
            Definition(Module + "Sidebar.", body: null),
            Definition(Module + "IProps#", body: null));

        var definitions = catalog.For("src/App.tsx");

        Assert.Equal(2, definitions.Count);
        Assert.All(definitions, definition => Assert.Null(definition.Body));
    }

    [Fact]
    public void Drops_what_no_boundary_can_be_read_for()
    {
        var catalog = Catalog(
            // Every property of every object literal in the repository. A vector per property is
            // the corpus bloat symbol-aware chunking exists to avoid.
            Definition(Module + "authority0:", body: null),
            // Anything scip-typescript declares inside a function body: no name in the id.
            Definition("scip-local src/App.tsx 3", body: null),
            // The Roslyn and XAML indexers report definitions with no body too — every x:Key in
            // every XAML file — and a resource key has no extent to infer.
            Definition("dotnet T:App.Views.MainWindow", body: null),
            // A parameter and a type parameter are not declarations to cut a file on.
            Definition(Module + "render().(props)", body: null),
            Definition(Module + "Box#[T]", body: null));

        Assert.Empty(catalog.For("src/App.tsx"));
        Assert.True(catalog.IsEmpty);
    }

    [Fact]
    public void Keeps_a_reported_body_whatever_the_indexer_calls_it()
    {
        // The scheme filter applies only to the inference. A body span is a fact, not a guess,
        // and every indexer that reports one is believed.
        var body = new SourceRange(0, 0, 4, 1);
        var catalog = Catalog(
            Definition("dotnet T:App.Views.MainWindow", body),
            Definition("scip-local src/App.tsx 3", body));

        Assert.Equal(2, catalog.For("src/App.tsx").Count);
    }

    private static SemanticOccurrence Definition(string symbolId, SourceRange? body) =>
        new()
        {
            DocumentPath = "src/App.tsx",
            Range = new SourceRange(0, 13, 0, 20),
            SymbolId = symbolId,
            Roles = SemanticOccurrenceRoles.Definition,
            Precision = NavigationPrecision.Precise,
            EnclosingRange = body,
        };

    private static SymbolDefinitionCatalog Catalog(params SemanticOccurrence[] occurrences) =>
        SymbolDefinitionCatalog.FromSemanticIndex(new SemanticIndex
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            BaseCommit = "commit",
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [new SemanticDocument { RelPath = "src/App.tsx", Hash = new byte[32] }],
            Symbols = [],
            Occurrences = [.. occurrences],
            Relationships = [],
        });
}
