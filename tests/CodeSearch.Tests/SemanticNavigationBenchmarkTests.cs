using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Semantics;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Tests;

public sealed class SemanticNavigationBenchmarkTests : IDisposable
{
    private const string BaseId = "dotnet M:Demo.A.Run";
    private const string ImplementationId = "dotnet M:Demo.B.Run";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "semantic-benchmark-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MeasuresMarkerBasedCorrectnessAndWarmLatency()
    {
        Write("Def.cs", "class A { public virtual void Run() {} }");
        Write("Impl.cs", "class B : A { public override void Run() {} }");
        Write("Use.cs", "new B().Run();");
        var suite = new SemanticBenchmarkSuite(
            SemanticBenchmarkSuite.CurrentSchemaVersion,
            Iterations: 3,
            Cases:
            [
                new SemanticBenchmarkCase(
                    "definition",
                    SemanticBenchmarkOperation.Definition,
                    Marker("Use.cs", "Run();"),
                    [Marker("Def.cs", "Run()")]),
                new SemanticBenchmarkCase(
                    "implementations",
                    SemanticBenchmarkOperation.Implementations,
                    Marker("Def.cs", "Run()"),
                    [Marker("Impl.cs", "Run()")]),
                new SemanticBenchmarkCase(
                    "relationship",
                    SemanticBenchmarkOperation.RelationshipsOutgoing,
                    Marker("Impl.cs", "Run()"),
                    [Marker("Def.cs", "Run()")],
                    RelationshipKind: SemanticRelationshipKind.Override),
            ]);

        var result = new SemanticNavigationBenchmark(Index()).Run(_root, suite);

        Assert.Equal(3, result.Passed);
        Assert.Equal(1, result.Correctness);
        Assert.All(result.Cases, item =>
        {
            Assert.True(item.Passed);
            Assert.True(item.FirstQueryMilliseconds >= 0);
            Assert.True(item.WarmP95Milliseconds >= item.WarmP50Milliseconds);
        });
    }

    private SemanticIndex Index() => new()
    {
        RepositoryId = "repo",
        GenerationId = "generation",
        GitTree = "tree",
        DirtyHash = null,
        IndexedAtUtc = DateTime.UnixEpoch,
        Documents = [Document("Def.cs"), Document("Impl.cs"), Document("Use.cs")],
        Symbols =
        [
            new SemanticSymbol
            {
                Id = BaseId,
                DisplayName = "Run",
                Kind = SemanticSymbolKind.Method,
            },
            new SemanticSymbol
            {
                Id = ImplementationId,
                DisplayName = "Run",
                Kind = SemanticSymbolKind.Method,
            },
        ],
        Occurrences =
        [
            Occurrence("Def.cs", "Run()", BaseId, SemanticOccurrenceRoles.Definition),
            Occurrence("Impl.cs", "Run()", ImplementationId, SemanticOccurrenceRoles.Definition),
            Occurrence("Use.cs", "Run();", BaseId, SemanticOccurrenceRoles.Reference),
        ],
        Relationships =
        [
            new SemanticRelationship
            {
                SourceSymbolId = ImplementationId,
                TargetSymbolId = BaseId,
                Kind = SemanticRelationshipKind.Override,
            },
        ],
    };

    private SemanticOccurrence Occurrence(
        string path,
        string marker,
        string symbolId,
        SemanticOccurrenceRoles roles)
    {
        var text = File.ReadAllText(Path.Combine(_root, path));
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        var position = SourceText.From(text).Lines.GetLinePosition(offset);
        return new SemanticOccurrence
        {
            DocumentPath = path,
            Range = new SourceRange(
                position.Line,
                position.Character,
                position.Line,
                position.Character + 3),
            SymbolId = symbolId,
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };
    }

    private SemanticDocument Document(string path) => new()
    {
        RelPath = path,
        Hash = SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, path))),
    };

    private static SemanticBenchmarkMarker Marker(string path, string marker) =>
        new(path, marker);

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, path), text, Encoding.UTF8);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
