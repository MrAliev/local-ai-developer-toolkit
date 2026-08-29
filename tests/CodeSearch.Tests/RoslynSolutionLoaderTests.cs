using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class RoslynSolutionLoaderTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-roslyn-loader-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Restores_project_dependencies_before_opening_the_workspace()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Fixture.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="System.Numerics.Tensors" Version="10.0.10" />
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Value.cs"),
            "public sealed class Value { }",
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(root, "obj", "project.assets.json")));

        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.True(File.Exists(Path.Combine(root, "obj", "project.assets.json")));
    }

    [Fact]
    public async Task Vulnerability_audit_does_not_block_semantic_workspace_loading()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Fixture.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="System.Text.Json" Version="8.0.0" />
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Value.cs"),
            "public sealed class Value { }",
            TestContext.Current.CancellationToken);

        var diagnostics = new List<string>();
        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            root,
            diagnostics.Add,
            TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Contains("NU190", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Without a solution file only one entry point is loaded, and the rest of the repository
    /// gets no precise navigation at all. That used to be a console line and nothing else, which
    /// reads exactly like full coverage from outside: the index is not empty and the status says
    /// precise. Naming them is what lets sync fail on it.
    /// </summary>
    [Fact]
    public async Task Projects_left_out_for_want_of_a_solution_are_named()
    {
        await WriteProjectAsync("First");
        await WriteProjectAsync("Second");

        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        var uncovered = Assert.Single(loaded.UncoveredProjects);
        // One of the two was chosen; the assertion is that the other is reported, whichever the
        // ordering picked.
        Assert.EndsWith(".csproj", uncovered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_single_project_leaves_nothing_out()
    {
        await WriteProjectAsync("Only");

        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.UncoveredProjects);
    }

    /// <summary>
    /// The line issue #139 turned on was a workspace failure rendered as its bare exception
    /// message — "Dll was not found.", no entry point, no severity — and five reproduction
    /// attempts could not trace it. A failure line must say what was being loaded when it
    /// happened.
    /// </summary>
    [Fact]
    public async Task A_workspace_failure_names_the_entry_point_being_loaded()
    {
        Directory.CreateDirectory(root);
        var solutionPath = Path.Combine(root, "Fixture.sln");
        // A solution naming a project file that does not exist: the load continues, and the
        // missing project arrives exactly as a workspace-failed diagnostic.
        await File.WriteAllTextAsync(
            solutionPath,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Missing", "Missing\Missing.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """,
            TestContext.Current.CancellationToken);

        var diagnostics = new List<string>();
        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            root,
            diagnostics.Add,
            TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Contains(
            diagnostics,
            diagnostic =>
                diagnostic.Contains("while loading", StringComparison.Ordinal) &&
                diagnostic.Contains(solutionPath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task WriteProjectAsync(string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, name + ".csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Value.cs"),
            $"namespace {name}; public sealed class Value {{ }}",
            TestContext.Current.CancellationToken);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
