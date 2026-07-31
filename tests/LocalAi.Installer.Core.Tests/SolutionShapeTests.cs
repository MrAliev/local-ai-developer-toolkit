using System.Xml.Linq;

namespace LocalAi.Installer.Core.Tests;

public sealed class SolutionShapeTests
{
    [Fact]
    public void Solution_contains_all_installer_projects()
    {
        var repositoryRoot = FindRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(repositoryRoot, "LocalAi.slnx"));
        var projectPaths = solution
            .Descendants("Project")
            .Select(project => project.Attribute("Path")?.Value.Replace('\\', '/'))
            .Where(path => path is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expectedProjectPaths = new[]
        {
            "src/LocalAi.Installer.Core/LocalAi.Installer.Core.csproj",
            "src/LocalAi.Installer/LocalAi.Installer.csproj",
            "tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj",
            "tests/LocalAi.Installer.Tests/LocalAi.Installer.Tests.csproj",
            "tests/LocalAi.Installer.IntegrationTests/LocalAi.Installer.IntegrationTests.csproj",
        };

        foreach (var expectedProjectPath in expectedProjectPaths)
        {
            Assert.Contains(expectedProjectPath, projectPaths);
        }
    }

    [Theory]
    [InlineData("src/LocalAi.Installer/LocalAi.Installer.csproj")]
    [InlineData("tests/LocalAi.Installer.Tests/LocalAi.Installer.Tests.csproj")]
    [InlineData("tests/LocalAi.Installer.IntegrationTests/LocalAi.Installer.IntegrationTests.csproj")]
    public void Localized_runtime_projects_disable_invariant_globalization(string projectPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(repositoryRoot, projectPath));
        var invariantGlobalization = project
            .Descendants("InvariantGlobalization")
            .SingleOrDefault()
            ?.Value;

        Assert.Equal("false", invariantGlobalization);
    }

    private static string FindRepositoryRoot()
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
