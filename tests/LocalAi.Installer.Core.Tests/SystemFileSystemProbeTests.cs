using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Tests;

public sealed class SystemFileSystemProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.FileSystemProbe.Tests",
        Guid.NewGuid().ToString("N"));
    private string? _linkPath;
    private string? _targetRoot;

    [Fact]
    public void Resolves_ancestor_symbolic_link_when_final_file_is_not_a_reparse_point()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows handle-based path resolution is Windows-specific.");
            return;
        }

        Directory.CreateDirectory(_root);
        _targetRoot = Path.Combine(
            Path.GetTempPath(),
            "LocalAi.FileSystemProbe.Target",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_targetRoot);
        var targetFile = Path.Combine(_targetRoot, "payload.dll");
        File.WriteAllText(targetFile, "payload");
        _linkPath = Path.Combine(_root, "linked");
        try
        {
            Directory.CreateSymbolicLink(_linkPath, _targetRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links requires Windows Developer Mode.");
            return;
        }

        var resolved = new SystemFileSystemProbe().ResolvePhysicalPath(
            Path.Combine(_linkPath, "payload.dll"));

        Assert.Equal(
            Path.GetFullPath(targetFile),
            resolved,
            ignoreCase: true);
    }

    public void Dispose()
    {
        if (_linkPath is not null && Directory.Exists(_linkPath))
        {
            Directory.Delete(_linkPath);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (_targetRoot is not null && Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }
}
