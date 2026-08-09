using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// The rule this exists to hold: on Windows a command with no extension is not a candidate.
///
/// npm writes two files for a command — a POSIX shell script with no extension and a .cmd shim.
/// The extensionless one exists, so a resolver that checks File.Exists first returns a path that
/// cannot be started, and the failure surfaces far away as "not a valid Win32 application". The
/// SCIP adapters were fixed for this; the language-server client kept its own copy of the
/// resolver and its own version of the bug.
/// </summary>
public sealed class ExecutableResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "executable-resolver-" + Guid.NewGuid().ToString("N"));

    public ExecutableResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_rooted_command_resolves_to_itself_when_it_exists()
    {
        var path = Path.Combine(_root, "tool.exe");
        File.WriteAllText(path, string.Empty);

        Assert.Equal(path, ExecutableResolver.Resolve(path));
    }

    [Fact]
    public void An_unknown_command_comes_back_unchanged()
    {
        // Returned as given so the operating system reports the command the caller asked for,
        // not a path this resolver invented.
        Assert.Equal("no-such-tool-xyz", ExecutableResolver.Resolve("no-such-tool-xyz"));
        Assert.Null(ExecutableResolver.Find("no-such-tool-xyz"));
    }

    [Fact]
    public void On_windows_a_command_shim_wins_over_the_extensionless_script()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PATHEXT semantics are Windows-specific.");
        var bare = Path.Combine(_root, "npm-tool");
        var shim = bare + ".cmd";
        // Exactly what npm leaves behind.
        File.WriteAllText(bare, "#!/bin/sh\n");
        File.WriteAllText(shim, "@echo off\n");

        var resolved = ExecutableResolver.Resolve(bare);

        Assert.Equal(shim, resolved);
    }

    [Fact]
    public void On_windows_an_extensionless_script_alone_is_not_executable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PATHEXT semantics are Windows-specific.");
        var bare = Path.Combine(_root, "posix-only");
        File.WriteAllText(bare, "#!/bin/sh\n");

        // It exists, and it still cannot be run. Reporting it as found is what produced the
        // confusing failure this resolver was written to avoid.
        Assert.Null(ExecutableResolver.Find(bare));
    }

    [Fact]
    public void Search_directories_lead_with_the_npm_prefix_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The npm prefix is a Windows path here.");
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm");

        Assert.Equal(expected, ExecutableResolver.SearchDirectories().First());
    }

    [Fact]
    public void Search_directories_are_distinct()
    {
        var directories = ExecutableResolver.SearchDirectories().ToArray();

        Assert.Equal(
            directories.Length,
            directories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
