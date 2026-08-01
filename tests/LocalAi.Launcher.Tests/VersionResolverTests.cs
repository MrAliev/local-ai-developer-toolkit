using LocalAi.Contracts;

namespace LocalAi.Launcher.Tests;

public sealed class VersionResolverTests
{
    [Theory]
    [InlineData("localai", "localai.exe")]
    [InlineData("codesearch", "codesearch.exe")]
    [InlineData("codesearch-mcp", "codesearch-mcp.exe")]
    [InlineData("locallm-mcp", "locallm-mcp.exe")]
    public void Resolves_every_allowlisted_tool_from_one_version(
        string tool,
        string executable)
    {
        using var install = TestInstall.CreateComplete("v1");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var resolved = new VersionResolver(install.BinRoot).Resolve(tool);

        Assert.Equal("v1", resolved.Version);
        Assert.Equal(
            Path.Combine(install.VersionsRoot, "v1", executable),
            resolved.ExecutablePath);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"version":"v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":".."}""")]
    [InlineData("""{"schemaVersion":1,"version":"sub\\v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":"C:\\escape"}""")]
    public void Rejects_unsupported_or_escaping_pointer(string json)
    {
        using var install = TestInstall.CreateComplete("v1");
        install.WriteCurrent(json);

        var error = Assert.Throws<LauncherException>(
            () => new VersionResolver(install.BinRoot).Resolve("localai"));

        Assert.Contains(
            error.Code,
            new[] { "current_pointer_invalid", "version_path_invalid" });
    }

    [Fact]
    public void Rejects_missing_pointer()
    {
        using var install = TestInstall.CreateComplete("v1");

        var error = Assert.Throws<LauncherException>(
            () => new VersionResolver(install.BinRoot).Resolve("localai"));

        Assert.Equal("current_pointer_missing", error.Code);
    }

    [Fact]
    public void Rejects_unknown_tool()
    {
        using var install = TestInstall.CreateComplete("v1");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var error = Assert.Throws<LauncherException>(
            () => new VersionResolver(install.BinRoot).Resolve("ollama"));

        Assert.Equal("tool_not_allowed", error.Code);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"version":"v1","unknown":true}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"version":"v1"}""")]
    public void Rejects_unknown_or_duplicate_pointer_members(string json)
    {
        using var install = TestInstall.CreateComplete("v1");
        install.WriteCurrent(json);

        var error = Assert.Throws<LauncherException>(
            () => new VersionResolver(install.BinRoot).Resolve("localai"));

        Assert.Equal("current_pointer_invalid", error.Code);
    }

    [Fact]
    public void Rejects_incomplete_version()
    {
        using var install = TestInstall.CreateComplete("v1");
        install.RemoveRequiredFile("v1", LocalAiPackageLayout.BrokerFile);
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var error = Assert.Throws<LauncherException>(
            () => new VersionResolver(install.BinRoot).Resolve("localai"));

        Assert.Equal("version_incomplete", error.Code);
    }

    [Theory]
    [InlineData("v1.")]
    [InlineData("v1 ")]
    [InlineData("CON")]
    [InlineData("../v1")]
    public void Rejects_unsafe_version_names_using_shared_contract(string version)
    {
        using var install = TestInstall.CreateComplete("v1");

        var error = Assert.Throws<LauncherException>(() =>
            new VersionResolver(install.BinRoot).ValidateVersion(version));

        Assert.Equal("version_path_invalid", error.Code);
        Assert.Equal("The LocalAi version name is invalid.", error.Message);
    }

    [Fact]
    public void Rejects_version_directory_reparse_escape()
    {
        using var install = TestInstall.CreateComplete("v1");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var candidate = install.VersionDirectory("v1");
        var outside = Path.Combine(install.Root, "outside");
        Directory.CreateDirectory(outside);
        string ResolvePhysicalPath(string path) =>
            string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase)
                ? outside
                : path;

        var error = Assert.Throws<LauncherException>(
            () => new VersionResolver(
                    install.BinRoot,
                    ResolvePhysicalPath)
                .Resolve("localai"));

        Assert.Equal("version_path_invalid", error.Code);
    }

    [Fact]
    public void Rejects_required_file_reparse_escape()
    {
        using var install = TestInstall.CreateComplete("v1");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var broker = Path.Combine(
            install.VersionDirectory("v1"),
            LocalAiPackageLayout.BrokerFile);
        var outside = Path.Combine(install.Root, "outside.dll");
        File.WriteAllText(outside, "outside");
        string ResolvePhysicalPath(string path) =>
            string.Equals(path, broker, StringComparison.OrdinalIgnoreCase)
                ? outside
                : path;

        var error = Assert.Throws<LauncherException>(
            () => new VersionResolver(
                    install.BinRoot,
                    ResolvePhysicalPath)
                .Resolve("localai"));

        Assert.Equal("version_path_invalid", error.Code);
    }
}
