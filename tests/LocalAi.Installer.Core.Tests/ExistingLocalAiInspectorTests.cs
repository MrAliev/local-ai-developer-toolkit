using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Tests;

public sealed class ExistingLocalAiInspectorTests : IDisposable
{
    private static readonly string[] RequiredFiles =
    [
        "localai.exe",
        "codesearch.exe",
        "codesearch-mcp.exe",
        "locallm-mcp.exe",
        "LocalAi.Broker.dll",
        "LocalAi.Contracts.dll",
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Classifies_missing_current_pointer_as_absent()
    {
        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Absent, snapshot.State);
    }

    [Fact]
    public void Classifies_complete_strict_pointer_as_compatible()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Compatible, snapshot.State);
        Assert.Equal("v1", snapshot.Version);
        Assert.Equal(Path.Combine(_root, "LocalAi", "bin", "versions", "v1"), snapshot.VersionPath);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"version":}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"version":"v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":"v1","unknown":true}""")]
    [InlineData("""{"schemaVersion":2,"version":"v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":".."}""")]
    [InlineData("""{"schemaVersion":1,"version":"sub/v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":"sub\\v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":"   "}""")]
    public void Classifies_malformed_unsupported_or_unsafe_pointer_as_unrecognized(string json)
    {
        Directory.CreateDirectory(BinRoot);
        WriteCurrent(json);

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.NotEmpty(snapshot.Reason!);
    }

    [Fact]
    public void Classifies_incomplete_version_as_unrecognized()
    {
        CreateComplete("v1");
        File.Delete(Path.Combine(VersionsRoot, "v1", RequiredFiles[0]));
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Equal("v1", snapshot.Version);
        Assert.Equal(Path.Combine(VersionsRoot, "v1"), snapshot.VersionPath);
        Assert.Contains("missing", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classifies_reparse_escape_as_unrecognized()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var versionPath = Path.Combine(VersionsRoot, "v1");
        var fileSystem = new RedirectingPhysicalPathFileSystem(
            versionPath,
            Path.Combine(_root, "outside"));

        var snapshot = new ExistingLocalAiInspector(fileSystem).Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Contains("outside", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string BinRoot => Path.Combine(_root, "LocalAi", "bin");
    private string VersionsRoot => Path.Combine(BinRoot, "versions");

    private ExistingLocalAiInspector CreateInspector() => new(new SystemFileSystemProbe());

    private void CreateComplete(string version)
    {
        var versionPath = Path.Combine(VersionsRoot, version);
        Directory.CreateDirectory(versionPath);
        foreach (var file in RequiredFiles)
        {
            File.WriteAllText(Path.Combine(versionPath, file), file);
        }
    }

    private void WriteCurrent(string json)
    {
        Directory.CreateDirectory(BinRoot);
        File.WriteAllText(Path.Combine(BinRoot, "current.json"), json);
    }

    private sealed class RedirectingPhysicalPathFileSystem(
        string redirectedPath,
        string physicalPath) : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);
        public string ResolvePhysicalPath(string path) =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(redirectedPath),
                StringComparison.OrdinalIgnoreCase)
                ? physicalPath
                : _inner.ResolvePhysicalPath(path);
    }
}
