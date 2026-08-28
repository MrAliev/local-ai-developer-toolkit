using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// Loads a real project from this repository through MSBuildWorkspace and requires that C#
/// semantics actually came back.
///
/// Pull request #129 moved Microsoft.CodeAnalysis.CSharp and .CSharp.Workspaces to 5.9.0 and left
/// Microsoft.CodeAnalysis.Workspaces.MSBuild on 5.6.0. The solution compiled, 1875 tests passed,
/// and every synchronization from then on indexed with no C# semantics at all — silently
/// degrading go_to_definition to the bounded text search that precise navigation exists to avoid.
/// A Git hook's console output caught it during an unrelated commit; nothing in the suite did.
///
/// Nothing was exercising the one path that binds those three packages together. The semantic
/// fixtures build their trees through AdhocWorkspace, which needs no MSBuild at all, and the two
/// loader tests assert only that LoadAsync returned — which a workspace holding zero projects
/// satisfies. The assertion that was missing is that something came back.
///
/// This test fails rather than skips, and takes no LOCALAI_STRICT_FIXTURES gate: its only
/// prerequisite is the .NET SDK that `dotnet test` already needed to reach this line. A skip here
/// would restore exactly the blindness the test exists to remove.
/// </summary>
public sealed class RealProjectSemanticsTests
{
    /// <summary>
    /// LocalAi.Contracts carries no PackageReference and no ProjectReference, so a failure here
    /// is about loading C# at all rather than about anything that project depends on.
    /// </summary>
    private const string ProjectDirectory = "LocalAi.Contracts";

    /// <summary>A type this project has to declare for the rest of the product to build.</summary>
    private const string KnownType = "LocalAiPackageLayout";

    [Fact]
    public async Task A_real_project_loads_with_documents_and_symbols()
    {
        var root = Path.Combine(RepositoryRoot(), "src", ProjectDirectory);
        var diagnostics = new List<string>();

        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            root,
            diagnostics.Add,
            TestContext.Current.CancellationToken);

        // LoadAsync returns null only when it found no project file to open, so there is nothing
        // for the workspace to have said about it.
        Assert.NotNull(loaded);

        var index = await loaded.BuildIndexAsync(
            root,
            new SemanticIndexBuildIdentity(
                RepositoryId: "real-project-semantics",
                GenerationId: "real-project-semantics",
                GitTree: "real-project-semantics",
                DirtyHash: null,
                BaseCommit: "real-project-semantics",
                IndexedAtUtc: DateTime.UnixEpoch),
            TestContext.Current.CancellationToken);

        Assert.True(
            index.Documents.Count > 0,
            $"The workspace opened but held no C# documents.{Report(diagnostics)}");

        // Reproducing the 5.6.0/5.9.0 split threw out of OpenProjectAsync, but the shape that
        // reached main is the quiet one: a workspace that returns with its projects unloaded and
        // an index with nothing in it. Both have to fail here, so count rather than catch.
        Assert.True(
            index.Symbols.Count > 0,
            "The workspace opened but produced no C# symbols. A split between the " +
            "Microsoft.CodeAnalysis.* package versions is what this looks like." +
            Report(diagnostics));

        Assert.True(
            index.Symbols.Any(symbol => string.Equals(
                symbol.DisplayName,
                KnownType,
                StringComparison.Ordinal)),
            $"C# semantics came back without '{KnownType}'. If that type was renamed, rename it " +
            $"here too; otherwise the project loaded only in part.{Report(diagnostics)}");
    }

    private static string Report(IReadOnlyCollection<string> diagnostics) =>
        diagnostics.Count == 0
            ? " The workspace reported no diagnostics."
            : " Workspace diagnostics: " + string.Join(" | ", diagnostics);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
