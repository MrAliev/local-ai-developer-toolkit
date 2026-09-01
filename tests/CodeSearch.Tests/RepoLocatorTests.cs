using CodeSearch.Core.Indexing;
using LocalAi.Contracts;
using System.Diagnostics;

namespace CodeSearch.Tests;

/// <summary>
/// The layer that answers "which checkout is this" and "which repository is that". Both answers
/// become directory names in the runtime, and both used to be assembled by hand from git output
/// — including a Replace('/', separator) that each reader had to remember for itself. The reader
/// that forgot is how a live worktree hashed to a key matching no directory, and its index was
/// deleted as unreachable. None of it was covered by a test.
/// </summary>
public sealed class RepoLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-locator-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A linked worktree resolves to the MAIN repository, and does so in the separators this
    /// platform uses. Git answers --git-common-dir with forward slashes on Windows, so the
    /// second half is the half that was repaired by hand.
    /// </summary>
    [Fact]
    public void A_linked_worktree_resolves_to_the_main_repository_in_native_separators()
    {
        var main = Checkout("main");
        var linked = Path.Combine(_root, "linked");
        Git(main, "worktree", "add", linked, "-b", "side");

        var resolved = RepoLocator.ResolveRoot(linked);

        Assert.Equal(FsPath.From(main), resolved);

        // Now guaranteed by the return type rather than by this method remembering to repair
        // git's output, so no mutation of the method body can break it. Kept as the written
        // form of that contract: if the signature ever goes back to string, this fails.
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain('/', resolved.Value);
        }
    }

    /// <summary>
    /// And the worktree resolves to ITSELF, because that is where its overlay lives. Confusing
    /// the two puts one checkout's overlay under another's key.
    /// </summary>
    [Fact]
    public void A_linked_worktree_is_its_own_working_root()
    {
        var main = Checkout("main");
        var linked = Path.Combine(_root, "linked");
        Git(main, "worktree", "add", linked, "-b", "side");

        Assert.Equal(FsPath.From(linked), RepoLocator.ResolveWorkingRoot(linked));
        Assert.Equal(FsPath.From(main), RepoLocator.ResolveWorkingRoot(main));
    }

    /// <summary>
    /// However the caller spelled it. A path with forward slashes, a trailing separator or a
    /// relative segment names one checkout, and one checkout has one overlay.
    /// </summary>
    [Fact]
    public void Every_spelling_of_a_checkout_resolves_to_one_working_root()
    {
        var main = Checkout("main");
        var nested = Path.Combine(main, "src", "deep");
        Directory.CreateDirectory(nested);

        var expected = FsPath.From(main);

        Assert.Equal(expected, RepoLocator.ResolveWorkingRoot(main.Replace(Path.DirectorySeparatorChar, '/')));
        Assert.Equal(expected, RepoLocator.ResolveWorkingRoot(main + Path.DirectorySeparatorChar));
        Assert.Equal(expected, RepoLocator.ResolveWorkingRoot(nested));
        Assert.Equal(
            expected,
            RepoLocator.ResolveWorkingRoot(Path.Combine(nested, "..", "..")));
    }

    /// <summary>
    /// A path to a file names the directory holding it, and the resolver that needs that is
    /// ResolveRoot rather than the working-root one: a file path handed to git as a working
    /// directory makes the subprocess fail, the failure is swallowed, and the fallback walk
    /// then stops at the worktree instead of reaching the repository it belongs to.
    ///
    /// Asserting it on ResolveWorkingRoot proves nothing — that method walks with DirectoryInfo,
    /// whose Parent is the containing directory anyway, so the step is invisible there. This
    /// test was written that way first and passed with the step removed.
    /// </summary>
    [Fact]
    public void A_file_inside_a_worktree_still_names_the_repository_it_belongs_to()
    {
        var main = Checkout("main");
        var linked = Path.Combine(_root, "linked");
        Git(main, "worktree", "add", linked, "-b", "side");

        Assert.Equal(
            FsPath.From(main),
            RepoLocator.ResolveRoot(Path.Combine(linked, "A.cs")));
        Assert.Equal(
            FsPath.From(linked),
            RepoLocator.ResolveWorkingRoot(Path.Combine(linked, "A.cs")));
    }

    private string Checkout(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "A.cs"), "class A { }");
        Git(directory, "init", "-b", "main");
        Git(directory, "config", "user.email", "tests@local.invalid");
        Git(directory, "config", "user.name", "LocalAi Tests");
        Git(directory, "add", "A.cs");
        Git(directory, "commit", "-m", "Initial");
        return directory;
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        // Read before waiting: a process whose pipe fills while nobody drains it never exits.
        // Copied helpers elsewhere in this suite wait first and get away with it only because
        // git says so little here.
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} exited {process.ExitCode}: {error}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup that throws fails the test it was added to protect.
        }
    }
}
