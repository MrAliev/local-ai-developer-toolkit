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
