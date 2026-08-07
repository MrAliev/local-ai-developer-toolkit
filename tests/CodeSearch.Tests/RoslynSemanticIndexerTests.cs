using CodeSearch.Core.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Tests;

public sealed class RoslynSemanticIndexerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-roslyn-indexer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Resolves_overloads_and_cross_project_references_precisely()
    {
        var (solution, appSource, _) = Solution();
        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            TestContext.Current.CancellationToken);
        var navigation = new SemanticNavigationService(index);
        var snapshot = new SemanticSnapshotIdentity("repo", "generation", "tree", null);

        var intCall = Position(appSource, "Work(42)");
        var stringCall = Position(appSource, "Work(\"x\")");
        var intDefinitions = navigation.GoToDefinition(
            "App/Worker.cs", intCall.Line, intCall.Character, snapshot);
        var stringDefinitions = navigation.GoToDefinition(
            "App/Worker.cs", stringCall.Line, stringCall.Character, snapshot);

        var intDefinition = Assert.Single(intDefinitions);
        var stringDefinition = Assert.Single(stringDefinitions);
        Assert.Equal("App/Worker.cs", intDefinition.DocumentPath);
        Assert.Equal("Lib/Base.cs", stringDefinition.DocumentPath);
        Assert.NotEqual(intDefinition.SymbolId, stringDefinition.SymbolId);
    }

    [Fact]
    public async Task Emits_override_and_interface_relationships()
    {
        var (solution, _, libSource) = Solution();

        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            TestContext.Current.CancellationToken);

        Assert.Contains(index.Relationships, relationship =>
            relationship.Kind == SemanticRelationshipKind.Override &&
            index.Symbols.Single(symbol => symbol.Id == relationship.SourceSymbolId)
                .Signature.Contains("Worker.Work(int)", StringComparison.Ordinal) &&
            index.Symbols.Single(symbol => symbol.Id == relationship.TargetSymbolId)
                .Signature.Contains("Base.Work(int)", StringComparison.Ordinal));
        Assert.Contains(index.Relationships, relationship =>
            relationship.Kind == SemanticRelationshipKind.Implementation &&
            index.Symbols.Single(symbol => symbol.Id == relationship.TargetSymbolId)
                .Signature.Contains("IWorker.Work(int)", StringComparison.Ordinal));

        Assert.Contains(index.Relationships, relationship =>
            relationship.Kind == SemanticRelationshipKind.Implementation &&
            index.Symbols.Single(symbol => symbol.Id == relationship.SourceSymbolId)
                .Signature.Contains("Worker", StringComparison.Ordinal) &&
            index.Symbols.Single(symbol => symbol.Id == relationship.TargetSymbolId)
                .Signature.Contains("IWorker", StringComparison.Ordinal));

        var interfacePosition = Position(libSource, "IWorker");
        var implementations = new SemanticNavigationService(index).FindImplementations(
            "Lib/Base.cs",
            interfacePosition.Line,
            interfacePosition.Character,
            new SemanticSnapshotIdentity("repo", "generation", "tree", null));

        Assert.Contains(implementations, implementation =>
            implementation.DocumentPath == "App/Worker.cs" &&
            index.Symbols.Single(symbol => symbol.Id == implementation.SymbolId)
                .Signature.Contains("Worker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_type_definitions_share_one_canonical_symbol()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new AdhocWorkspace();
        var project = AddProject(workspace.CurrentSolution, "Partial");
        var solution = project.Solution
            .AddDocument(DocumentId.CreateNewId(project.Id), "One.cs",
                SourceText.From("namespace Demo; public partial class Pair { }"),
                filePath: Path.Combine(_root, "One.cs"))
            .AddDocument(DocumentId.CreateNewId(project.Id), "Two.cs",
                SourceText.From("namespace Demo; public partial class Pair { }"),
                filePath: Path.Combine(_root, "Two.cs"));

        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            TestContext.Current.CancellationToken);
        var pairDefinitions = index.Occurrences.Where(occurrence =>
            occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) &&
            index.Symbols.Single(symbol => symbol.Id == occurrence.SymbolId).DisplayName == "Pair")
            .ToArray();

        Assert.Equal(2, pairDefinitions.Length);
        Assert.Single(pairDefinitions.Select(occurrence => occurrence.SymbolId).Distinct());
    }

    [Fact]
    public async Task Alias_references_resolve_to_the_alias_definition()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new AdhocWorkspace();
        var project = AddProject(workspace.CurrentSolution, "Aliases");
        const string source =
            """
            using PairAlias = Demo.Pair<int>;
            namespace Demo { public class Pair<T> { public T Echo(T value) => value; } }
            public class Use { public int Run(PairAlias pair) => pair.Echo(1); }
            """;
        var solution = project.Solution.AddDocument(
            DocumentId.CreateNewId(project.Id),
            "Aliases.cs",
            SourceText.From(source),
            filePath: Path.Combine(_root, "Aliases.cs"));
        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            TestContext.Current.CancellationToken);
        var position = Position(source, "PairAlias pair");
        var definitions = new SemanticNavigationService(index).GoToDefinition(
            "Aliases.cs",
            position.Line,
            position.Character,
            new SemanticSnapshotIdentity("repo", "generation", "tree", null));

        var definition = Assert.Single(definitions);
        Assert.Equal(Position(source, "PairAlias ="), new LinePosition(
            definition.Range.StartLine,
            definition.Range.StartCharacter));
    }

    [Fact]
    public async Task Linked_source_in_multiple_projects_is_indexed_once()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new AdhocWorkspace();
        const string source = "namespace Shared; public sealed class Linked { }";
        var sharedPath = Path.Combine(_root, "Shared", "Linked.cs");
        var first = AddProject(workspace.CurrentSolution, "First");
        var solution = first.Solution.AddDocument(
            DocumentId.CreateNewId(first.Id),
            "Linked.cs",
            SourceText.From(source),
            filePath: sharedPath);
        var second = AddProject(solution, "Second");
        solution = second.Solution.AddDocument(
            DocumentId.CreateNewId(second.Id),
            "Linked.cs",
            SourceText.From(source),
            filePath: sharedPath);

        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            TestContext.Current.CancellationToken);

        Assert.Single(index.Documents);
        Assert.Equal("Shared/Linked.cs", index.Documents[0].RelPath);
        Assert.Single(index.Occurrences, occurrence =>
            occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) &&
            index.Symbols.Single(symbol => symbol.Id == occurrence.SymbolId).DisplayName == "Linked");
        index.NormalizeForUse();
    }

    private (Microsoft.CodeAnalysis.Solution Solution, string AppSource, string LibSource) Solution()
    {
        Directory.CreateDirectory(_root);
        var workspace = new AdhocWorkspace();
        var lib = AddProject(workspace.CurrentSolution, "Lib");
        const string libSource =
            """
            namespace Lib;
            public interface IWorker { int Work(int value); }
            public class Base
            {
                public virtual int Work(int value) => value;
                public int Work(string value) => value.Length;
            }
            """;
        var solution = lib.Solution.AddDocument(
            DocumentId.CreateNewId(lib.Id),
            "Base.cs",
            SourceText.From(libSource),
            filePath: Path.Combine(_root, "Lib", "Base.cs"));
        var app = AddProject(solution, "App");
        solution = app.Solution.AddProjectReference(app.Id, new ProjectReference(lib.Id));
        const string appSource =
            """
            using Lib;
            public class Worker : Base, IWorker
            {
                public override int Work(int value) => base.Work(value);
                int IWorker.Work(int value) => Work(value);
                public int Run() => Work(42) + Work("x");
            }
            """;
        solution = solution.AddDocument(
            DocumentId.CreateNewId(app.Id),
            "Worker.cs",
            SourceText.From(appSource),
            filePath: Path.Combine(_root, "App", "Worker.cs"));
        return (solution, appSource, libSource);
    }

    private static Project AddProject(Microsoft.CodeAnalysis.Solution solution, string name)
    {
        var id = ProjectId.CreateNewId(name);
        var info = ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            metadataReferences: TrustedPlatformReferences());
        return solution.AddProject(info).GetProject(id)!;
    }

    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    private static SemanticIndexBuildIdentity Identity() =>
        new(
            "repo",
            "generation",
            "tree",
            null,
            "commit",
            new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc));

    private static LinePosition Position(string source, string token)
    {
        var offset = source.IndexOf(token, StringComparison.Ordinal);
        Assert.True(offset >= 0);
        return SourceText.From(source).Lines.GetLinePosition(offset);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
