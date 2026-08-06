using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public class SemanticIndexTests
{
    [Fact]
    public void RoundTripsEveryFieldAndWritesDeterministically()
    {
        var original = SampleIndex();
        var first = TempPath();
        var second = TempPath();
        try
        {
            original.Save(first);
            original.Save(second);

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

            var loaded = SemanticIndex.Load(first);
            Assert.Equal("repository", loaded.RepositoryId);
            Assert.Equal("generation", loaded.GenerationId);
            Assert.Equal("tree", loaded.GitTree);
            Assert.Equal("dirty", loaded.DirtyHash);
            Assert.Equal(original.IndexedAtUtc, loaded.IndexedAtUtc);

            Assert.Equal(["Src/A.cs", "Src/Б.cs"], loaded.Documents.Select(document => document.RelPath));
            Assert.Equal(["scip-dotnet pkg MyApp 1.0.0 A#", "scip-dotnet pkg MyApp 1.0.0 A#Go()."],
                loaded.Symbols.Select(symbol => symbol.Id));
            Assert.Equal(3, loaded.Occurrences.Count);
            Assert.Equal(SemanticOccurrenceRoles.Definition, loaded.Occurrences[0].Roles);
            Assert.Equal(NavigationPrecision.Precise, loaded.Occurrences[0].Precision);
            Assert.Single(loaded.Relationships);
            Assert.Equal(SemanticRelationshipKind.Implementation, loaded.Relationships[0].Kind);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void RejectsPathsOutsideTheRepository()
    {
        var index = SampleIndex() with
        {
            Documents =
            [
                new SemanticDocument
                {
                    RelPath = "../outside.cs",
                    Hash = new byte[32],
                },
            ],
            Occurrences = [],
        };

        var path = TempPath();
        try
        {
            Assert.Throws<InvalidDataException>(() => index.Save(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsAnInvalidSourceRange()
    {
        var index = SampleIndex() with
        {
            Occurrences =
            [
                new SemanticOccurrence
                {
                    DocumentPath = "Src/A.cs",
                    Range = new SourceRange(4, 2, 3, 9),
                    SymbolId = "scip-dotnet pkg MyApp 1.0.0 A#",
                    Roles = SemanticOccurrenceRoles.Reference,
                    Precision = NavigationPrecision.Precise,
                },
            ],
        };

        var path = TempPath();
        try
        {
            Assert.Throws<InvalidDataException>(() => index.Save(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsAFileThatIsNotASemanticIndex()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "not a semantic index");
            Assert.Throws<InvalidDataException>(() => SemanticIndex.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SemanticIndex SampleIndex() =>
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
                new SemanticDocument
                {
                    RelPath = "Src/Б.cs",
                    Hash = Enumerable.Repeat((byte)7, 32).ToArray(),
                },
                new SemanticDocument
                {
                    RelPath = "Src/A.cs",
                    Hash = new byte[32],
                },
            ],
            Symbols =
            [
                new SemanticSymbol
                {
                    Id = "scip-dotnet pkg MyApp 1.0.0 A#Go().",
                    DisplayName = "Go",
                    Kind = SemanticSymbolKind.Method,
                    Signature = "void A.Go()",
                    Documentation = "Runs the operation.",
                },
                new SemanticSymbol
                {
                    Id = "scip-dotnet pkg MyApp 1.0.0 A#",
                    DisplayName = "A",
                    Kind = SemanticSymbolKind.Type,
                    Signature = "class A",
                },
            ],
            Occurrences =
            [
                new SemanticOccurrence
                {
                    DocumentPath = "Src/Б.cs",
                    Range = new SourceRange(2, 4, 2, 5),
                    SymbolId = "scip-dotnet pkg MyApp 1.0.0 A#Go().",
                    Roles = SemanticOccurrenceRoles.Reference | SemanticOccurrenceRoles.Read,
                    Precision = NavigationPrecision.Precise,
                },
                new SemanticOccurrence
                {
                    DocumentPath = "Src/A.cs",
                    Range = new SourceRange(0, 13, 0, 14),
                    SymbolId = "scip-dotnet pkg MyApp 1.0.0 A#",
                    Roles = SemanticOccurrenceRoles.Definition,
                    Precision = NavigationPrecision.Precise,
                },
                new SemanticOccurrence
                {
                    DocumentPath = "Src/A.cs",
                    Range = new SourceRange(2, 16, 2, 18),
                    SymbolId = "scip-dotnet pkg MyApp 1.0.0 A#Go().",
                    Roles = SemanticOccurrenceRoles.Definition,
                    Precision = NavigationPrecision.Precise,
                },
            ],
            Relationships =
            [
                new SemanticRelationship
                {
                    SourceSymbolId = "scip-dotnet pkg MyApp 1.0.0 A#Go().",
                    TargetSymbolId = "scip-dotnet pkg MyApp 1.0.0 A#",
                    Kind = SemanticRelationshipKind.Implementation,
                },
            ],
        };

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"codesearch-semantic-{Guid.NewGuid():N}.sidx");
}
