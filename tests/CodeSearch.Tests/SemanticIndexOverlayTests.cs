using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public class SemanticIndexOverlayTests
{
    private const string Symbol = "scip-dotnet pkg App 1.0.0 C#Go().";
    private static readonly DateTime IndexedAt =
        new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RoundTripsAndMaterializesChangedDefinitionsForNavigation()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Def.cs", 1), Document("Use.cs", 2)],
            [Method(Symbol)],
            [Definition("Def.cs", Symbol, 1), Reference("Use.cs", Symbol, 4)]);
        var current = Index(
            "working-tree",
            "dirty-hash",
            [Document("Def.cs", 3), Document("Use.cs", 2)],
            [Method(Symbol)],
            [Definition("Def.cs", Symbol, 7), Reference("Use.cs", Symbol, 4)]);
        var path = TempPath();
        try
        {
            SemanticIndexOverlay.Create(@base, current, ["Def.cs"]).Save(path);

            var loaded = SemanticIndexOverlay.Load(path);
            var materialized = loaded.Materialize(@base);
            var service = new SemanticNavigationService(materialized);
            var definition = Assert.Single(service.GoToDefinition(
                "Use.cs",
                4,
                1,
                new SemanticSnapshotIdentity("repository", "generation", "working-tree", "dirty-hash")));

            Assert.Equal("Def.cs", definition.DocumentPath);
            Assert.Equal(7, definition.Range.StartLine);
            Assert.Equal(["Def.cs"], loaded.Changed.Documents.Select(document => document.RelPath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TombstoneRemovesADeletedDocumentAndItsUnusedSymbol()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Deleted.cs", 1), Document("Keep.cs", 2)],
            [Method(Symbol)],
            [Definition("Deleted.cs", Symbol, 1)]);
        var current = Index(
            "working-tree",
            "dirty-hash",
            [Document("Keep.cs", 2)],
            [],
            []);

        var overlay = SemanticIndexOverlay.Create(@base, current, ["Deleted.cs"]);
        var materialized = overlay.Materialize(@base);

        Assert.Equal(["Deleted.cs"], overlay.DeletedDocuments);
        Assert.Equal(["Keep.cs"], materialized.Documents.Select(document => document.RelPath));
        Assert.Empty(materialized.Occurrences);
        Assert.Empty(materialized.Symbols);
    }

    [Fact]
    public void IncludesAnUnchangedDocumentWhenItsResolvedSymbolChanges()
    {
        const string replacement = "scip-dotnet pkg App 1.0.0 C#Run().";
        var @base = Index(
            "base-tree",
            null,
            [Document("Def.cs", 1), Document("Use.cs", 2)],
            [Method(Symbol)],
            [Definition("Def.cs", Symbol, 1), Reference("Use.cs", Symbol, 4)]);
        var current = Index(
            "working-tree",
            "dirty-hash",
            [Document("Def.cs", 3), Document("Use.cs", 2)],
            [Method(replacement)],
            [Definition("Def.cs", replacement, 1), Reference("Use.cs", replacement, 4)]);

        var overlay = SemanticIndexOverlay.Create(@base, current, ["Def.cs"]);
        var materialized = overlay.Materialize(@base);

        Assert.Equal(
            ["Def.cs", "Use.cs"],
            overlay.Changed.Documents.Select(document => document.RelPath));
        Assert.All(materialized.Occurrences, occurrence => Assert.Equal(replacement, occurrence.SymbolId));
    }

    [Fact]
    public void RejectsMaterializationAgainstAnotherBaseTree()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("A.cs", 1)],
            [Method(Symbol)],
            [Definition("A.cs", Symbol, 1)]);
        var current = Index(
            "working-tree",
            "dirty-hash",
            [Document("A.cs", 2)],
            [Method(Symbol)],
            [Definition("A.cs", Symbol, 2)]);
        var overlay = SemanticIndexOverlay.Create(@base, current, ["A.cs"]);

        Assert.Throws<SemanticSnapshotMismatchException>(
            () => overlay.Materialize(@base with { GitTree = "wrong-tree" }));
    }

    [Fact]
    public void StoresOnlyTheChangedSlice()
    {
        var documents = Enumerable.Range(0, 200)
            .Select(index => Document($"Src/{index:D3}.cs", index))
            .ToList();
        var occurrences = documents.Select((document, index) =>
                Definition(document.RelPath, $"symbol-{index}", index))
            .ToList();
        var symbols = Enumerable.Range(0, 200).Select(index => Method($"symbol-{index}")).ToList();
        var @base = Index("base-tree", null, documents, symbols, occurrences);
        var currentDocuments = documents
            .Select(document => document.RelPath == "Src/123.cs" ? Document(document.RelPath, 250) : document)
            .ToList();
        var currentOccurrences = occurrences
            .Select(occurrence => occurrence.DocumentPath == "Src/123.cs"
                ? occurrence with { Range = new SourceRange(999, 0, 999, 2) }
                : occurrence)
            .ToList();
        var current = Index("working-tree", "dirty-hash", currentDocuments, symbols, currentOccurrences);
        var overlayPath = TempPath();
        var fullPath = TempPath();
        try
        {
            SemanticIndexOverlay.Create(@base, current, ["Src/123.cs"]).Save(overlayPath);
            current.Save(fullPath);

            Assert.True(new FileInfo(overlayPath).Length < new FileInfo(fullPath).Length / 5);
        }
        finally
        {
            File.Delete(overlayPath);
            File.Delete(fullPath);
        }
    }

    private static SemanticIndex Index(
        string gitTree,
        string? dirtyHash,
        List<SemanticDocument> documents,
        List<SemanticSymbol> symbols,
        List<SemanticOccurrence> occurrences) =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = gitTree,
            DirtyHash = dirtyHash,
            BaseCommit = "commit",
            IndexedAtUtc = IndexedAt,
            Documents = documents,
            Symbols = symbols,
            Occurrences = occurrences,
            Relationships = [],
        };

    private static SemanticDocument Document(string path, int value) =>
        new()
        {
            RelPath = path,
            Hash = Enumerable.Repeat(unchecked((byte)value), 32).ToArray(),
        };

    private static SemanticSymbol Method(string id) =>
        new()
        {
            Id = id,
            DisplayName = "Go",
            Kind = SemanticSymbolKind.Method,
            Signature = "void Go()",
        };

    private static SemanticOccurrence Definition(string path, string symbol, int line) =>
        Occurrence(path, symbol, line, SemanticOccurrenceRoles.Definition);

    private static SemanticOccurrence Reference(string path, string symbol, int line) =>
        Occurrence(path, symbol, line, SemanticOccurrenceRoles.Reference);

    private static SemanticOccurrence Occurrence(
        string path,
        string symbol,
        int line,
        SemanticOccurrenceRoles roles) =>
        new()
        {
            DocumentPath = path,
            Range = new SourceRange(line, 0, line, 2),
            SymbolId = symbol,
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"semantic-overlay-{Guid.NewGuid():N}.bin");
}
