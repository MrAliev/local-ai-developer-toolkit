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

    [Fact]
    public void Rejects_versions_root_that_physically_escapes_bin_root()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var fileSystem = new PrefixRedirectingPhysicalPathFileSystem(
            VersionsRoot,
            Path.Combine(_root, "outside", "versions"));

        var snapshot = new ExistingLocalAiInspector(fileSystem).Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Contains("outside", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Revalidates_version_directory_after_required_files_are_checked()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var versionPath = Path.Combine(VersionsRoot, "v1");
        var fileSystem = new SwappingPhysicalPathFileSystem(
            versionPath,
            versionPath,
            Path.Combine(_root, "outside", "v1"));

        var snapshot = new ExistingLocalAiInspector(fileSystem).Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Contains("changed", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classifies_native_physical_path_failure_as_unrecognized()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var snapshot = new ExistingLocalAiInspector(
                new FailingPhysicalPathFileSystem())
            .Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Contains("physical path failed", snapshot.Reason!);
    }

    [Fact]
    public void Rejects_real_versions_ancestor_link_escape_when_links_are_supported()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "LocalAi.Installer.Outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BinRoot);
        Directory.CreateDirectory(outsideRoot);
        var outsideVersion = Path.Combine(outsideRoot, "v1");
        Directory.CreateDirectory(outsideVersion);
        foreach (var file in RequiredFiles)
        {
            File.WriteAllText(Path.Combine(outsideVersion, file), file);
        }

        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(VersionsRoot, outsideRoot);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("Creating symbolic links requires Windows Developer Mode.");
                return;
            }

            var snapshot = CreateInspector().Inspect(_root);

            Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
            Assert.Contains("outside", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(VersionsRoot))
            {
                Directory.Delete(VersionsRoot);
            }

            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
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

    private sealed class SwappingPhysicalPathFileSystem(
        string swappedPath,
        string firstPhysicalPath,
        string laterPhysicalPath) : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();
        private int _resolveCount;

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);

        public string ResolvePhysicalPath(string path)
        {
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(path),
                    Path.TrimEndingDirectorySeparator(swappedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return _inner.ResolvePhysicalPath(path);
            }

            return Interlocked.Increment(ref _resolveCount) == 1
                ? firstPhysicalPath
                : laterPhysicalPath;
        }
    }

    private sealed class PrefixRedirectingPhysicalPathFileSystem(
        string logicalRoot,
        string physicalRoot) : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);

        public string ResolvePhysicalPath(string path)
        {
            var relative = Path.GetRelativePath(logicalRoot, path);
            return relative == "." ||
                   (!relative.StartsWith("..", StringComparison.Ordinal) &&
                    !Path.IsPathRooted(relative))
                ? Path.GetFullPath(Path.Combine(physicalRoot, relative))
                : _inner.ResolvePhysicalPath(path);
        }
    }

    private sealed class FailingPhysicalPathFileSystem : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);
        public string ResolvePhysicalPath(string path) =>
            throw new System.ComponentModel.Win32Exception(
                "physical path failed");
    }
}
