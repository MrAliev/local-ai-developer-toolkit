using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

/// <summary>
/// Covers the guard that stops an indexed path escaping the repository through a link.
///
/// These run everywhere. The tests that create real symbolic links need Windows Developer Mode
/// or administrator rights and skip without them, which meant the guard itself was exercised
/// only on a privileged machine and never on a CI runner. A guard against reading outside the
/// repository is the wrong thing to leave untested by default, so the walk takes its view of the
/// filesystem as a parameter and these supply it.
///
/// The real-link tests stay where they are. They prove that a real link actually carries the
/// attribute this walk looks for — the one thing a fake cannot establish.
/// </summary>
public sealed class SafeSourcePathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safe-source-path-" + Guid.NewGuid().ToString("N"));

    public SafeSourcePathTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "nested"));
        File.WriteAllText(Path.Combine(_root, "src", "nested", "File.cs"), "class A {}");
    }

    [Fact]
    public void An_ordinary_path_inside_the_repository_resolves()
    {
        var resolved = SafeSourcePath.TryResolveExisting(
            _root,
            Path.Combine("src", "nested", "File.cs"),
            out var fullPath,
            out var failure,
            Ordinary);

        Assert.True(resolved);
        Assert.Equal(SourcePathFailure.None, failure);
        Assert.Equal(Path.Combine(_root, "src", "nested", "File.cs"), fullPath);
    }

    /// <summary>
    /// The interesting case, and the one a lexical check misses entirely: every component is
    /// inside the repository by name, and one of them is a door out of it.
    /// </summary>
    [Fact]
    public void A_reparse_point_on_an_ancestor_directory_stops_the_walk()
    {
        var linked = Path.Combine(_root, "src", "nested");

        var resolved = SafeSourcePath.TryResolveExisting(
            _root,
            Path.Combine("src", "nested", "File.cs"),
            out _,
            out var failure,
            path => ReparseAt(path, linked));

        Assert.False(resolved);
        Assert.Equal(SourcePathFailure.ReparsePoint, failure);
    }

    [Fact]
    public void A_reparse_point_on_the_file_itself_stops_the_walk()
    {
        var linked = Path.Combine(_root, "src", "nested", "File.cs");

        var resolved = SafeSourcePath.TryResolveExisting(
            _root,
            Path.Combine("src", "nested", "File.cs"),
            out _,
            out var failure,
            path => ReparseAt(path, linked));

        Assert.False(resolved);
        Assert.Equal(SourcePathFailure.ReparsePoint, failure);
    }

    /// <summary>
    /// A link on a sibling the walk never touches must not fail an unrelated path — otherwise the
    /// guard would be refusing on the mere presence of a link anywhere in the tree.
    /// </summary>
    [Fact]
    public void A_reparse_point_the_walk_never_visits_is_irrelevant()
    {
        var elsewhere = Path.Combine(_root, "src", "other");

        var resolved = SafeSourcePath.TryResolveExisting(
            _root,
            Path.Combine("src", "nested", "File.cs"),
            out _,
            out var failure,
            path => ReparseAt(path, elsewhere));

        Assert.True(resolved);
        Assert.Equal(SourcePathFailure.None, failure);
    }

    [Theory]
    [InlineData("../Outside.cs")]
    [InlineData("..")]
    [InlineData("src/../../Outside.cs")]
    public void A_path_that_leaves_the_repository_lexically_is_refused(string relativePath)
    {
        var resolved = SafeSourcePath.TryResolveExisting(
            _root,
            relativePath,
            out _,
            out var failure,
            Ordinary);

        Assert.False(resolved);
        Assert.Equal(SourcePathFailure.OutsideRoot, failure);
    }

    [Fact]
    public void A_component_that_does_not_exist_is_reported_as_missing()
    {
        var resolved = SafeSourcePath.TryResolveExisting(
            _root,
            Path.Combine("src", "absent", "File.cs"),
            out _,
            out var failure,
            path => path.EndsWith("absent", StringComparison.Ordinal)
                ? throw new DirectoryNotFoundException(path)
                : FileAttributes.Directory);

        Assert.False(resolved);
        Assert.Equal(SourcePathFailure.Missing, failure);
    }

    /// <summary>An unreadable component is refused rather than treated as safe.</summary>
    [Fact]
    public void A_component_that_cannot_be_read_is_refused()
    {
        var resolved = SafeSourcePath.TryResolveExisting(
            _root,
            Path.Combine("src", "nested", "File.cs"),
            out _,
            out var failure,
            path => path.EndsWith("nested", StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException(path)
                : FileAttributes.Directory);

        Assert.False(resolved);
        Assert.Equal(SourcePathFailure.Unavailable, failure);
    }

    private static FileAttributes Ordinary(string path) =>
        File.Exists(path) ? FileAttributes.Normal : FileAttributes.Directory;

    private static FileAttributes ReparseAt(string path, string linked) =>
        string.Equals(path, linked, StringComparison.OrdinalIgnoreCase)
            ? FileAttributes.ReparsePoint | FileAttributes.Directory
            : Ordinary(path);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
