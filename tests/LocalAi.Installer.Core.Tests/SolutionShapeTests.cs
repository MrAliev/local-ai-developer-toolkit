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

    [Theory]
    [InlineData("ValidateExecutableReferencesMatchSelfContained")]
    [InlineData("_GetChildProjectCopyToPublishDirectoryItems")]
    public void Wpf_project_avoids_broad_executable_reference_workarounds(string propertyName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(
            Path.Combine(repositoryRoot, "src", "LocalAi.Installer", "LocalAi.Installer.csproj"));

        Assert.Empty(project.Descendants(propertyName));
    }

    [Theory]
    [InlineData("../LocalAi.Launcher/LocalAi.Launcher.csproj")]
    [InlineData("../LocalLm.Core/LocalLm.Core.csproj")]
    public void Core_executable_dependency_references_are_build_order_only(string expectedProjectPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(
            Path.Combine(repositoryRoot, "src", "LocalAi.Installer.Core", "LocalAi.Installer.Core.csproj"));
        var projectReference = project
            .Descendants("ProjectReference")
            .Single(reference =>
                reference.Attribute("Include")?.Value.Replace('\\', '/') == expectedProjectPath);

        Assert.Equal("false", projectReference.Attribute("ReferenceOutputAssembly")?.Value);
        Assert.Equal("false", projectReference.Attribute("Private")?.Value);
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
