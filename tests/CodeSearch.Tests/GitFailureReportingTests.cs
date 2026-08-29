using System.Diagnostics;
using CodeSearch.Core.Indexing;
using CodeSearch.Mcp;

namespace CodeSearch.Tests;

/// <summary>
/// Six agent sessions sharing one MCP server saw <c>get_code_chunk</c> fail three times in
/// ninety-nine calls: twice with an error that named nothing, once with "Git common directory is
/// unavailable". Retrying the same chunk id immediately afterwards worked every time.
///
/// The report suspected a shared libgit2 handle. There is none — every git question is a child
/// process — so what was left was a message that could not tell "there is no repository here"
/// from "git could not be run just now", and sent whoever read it to look in the wrong place.
/// </summary>
public sealed class GitFailureReportingTests : IDisposable
{
    private const string CommonDirectoryQuery =
        "rev-parse --path-format=absolute --git-common-dir";

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-git-failure-" + Guid.NewGuid().ToString("N"));

    public GitFailureReportingTests() => Directory.CreateDirectory(root);

    [Fact]
    public void A_directory_that_is_not_a_repository_says_which_call_failed_and_why()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RepoLocator.GitOutputOrThrow(root, CommonDirectoryQuery, "The git common directory"));

        Assert.Contains(
            "The git common directory could not be read",
            exception.Message,
            StringComparison.Ordinal);
        // The call, so the reader knows what was asked.
        Assert.Contains("git rev-parse", exception.Message, StringComparison.Ordinal);
        // git's own words, so they are not left guessing what it objected to.
        Assert.Contains(
            "not a git repository",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sentence that sent the original report down the wrong path. It asserted something
    /// about the repository, and the repository was fine.
    /// </summary>
    [Fact]
    public void A_failure_no_longer_claims_the_common_directory_is_simply_unavailable()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RepoLocator.GitOutputOrThrow(root, CommonDirectoryQuery, "The git common directory"));

        Assert.DoesNotContain(
            "Git common directory is unavailable.",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The path the report actually came in on: Inspect is what get_code_chunk reaches for, and
    /// it is where the misleading sentence was produced.
    /// </summary>
    [Fact]
    public void Inspecting_a_directory_that_is_not_a_repository_explains_itself()
    {
        var runtimeRoot = Path.Combine(root, "runtime");

        var exception = Record.Exception(() => RuntimeIndexLayout.Inspect(root, runtimeRoot));

        Assert.NotNull(exception);
        Assert.DoesNotContain(
            "Git common directory is unavailable.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_that_answers_returns_its_answer()
    {
        InitialiseRepository();

        var commonDirectory = RepoLocator.GitOutputOrThrow(
            root,
            CommonDirectoryQuery,
            "The git common directory");

        Assert.EndsWith(".git", commonDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Several clients asking at once, which is the condition the report was made under. This
    /// cannot reproduce a missed deadline on an idle machine, and does not claim to: what it
    /// holds is that reading git carries no shared state between callers, which is the thing the
    /// report suspected and the thing a later change could accidentally introduce.
    /// </summary>
    [Fact]
    public async Task Concurrent_readers_of_one_working_tree_all_get_the_same_answer()
    {
        InitialiseRepository();

        var heads = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            RepoLocator.GitOutputOrThrow(root, "rev-parse HEAD", "Git HEAD"))));

        Assert.All(heads, head => Assert.Equal(heads[0], head));
        Assert.NotEmpty(heads[0]);
    }

    /// <summary>
    /// Two of the three reported failures came back with a message that named nothing. A type
    /// always exists, so it is always said.
    /// </summary>
    [Fact]
    public void An_exception_with_no_message_is_still_named()
    {
        Assert.Equal(
            nameof(InvalidOperationException),
            CodeSearchTools.Describe(new InvalidOperationException(string.Empty)));
    }

    [Fact]
    public void A_described_exception_carries_its_type_and_its_message()
    {
        var described = CodeSearchTools.Describe(new IOException("the file is in use"));

        Assert.Contains(nameof(IOException), described, StringComparison.Ordinal);
        Assert.Contains("the file is in use", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The outer exception is often a wrapper whose message says the least of the two.
    /// </summary>
    [Fact]
    public void A_wrapped_cause_is_reported_underneath_its_wrapper()
    {
        var described = CodeSearchTools.Describe(
            new InvalidOperationException(
                "the chunk could not be read",
                new UnauthorizedAccessException("access to the path is denied")));

        Assert.Contains("the chunk could not be read", described, StringComparison.Ordinal);
        Assert.Contains(nameof(UnauthorizedAccessException), described, StringComparison.Ordinal);
        Assert.Contains("access to the path is denied", described, StringComparison.Ordinal);
    }

    private void InitialiseRepository()
    {
        File.WriteAllText(Path.Combine(root, "A.cs"), "class A { }\r\n");
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@local.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("add", "A.cs");
        Git("commit", "-m", "Initial");
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git could not be started.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited {process.ExitCode}: " +
                process.StandardError.ReadToEnd());
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // Git marks objects read-only, which Directory.Delete refuses to remove.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }
}
