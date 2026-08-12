using LocalAi.Installer.Core.Abstractions;
using LocalAi.ReleaseSigner;

namespace LocalAi.ReleaseSigner.Tests;

/// <summary>
/// Drives the preparation half against a real repository with a real remote.
///
/// The rest of the suite hands the command a scripted process runner, which answers `git status`
/// from a table rather than from a working tree. That is the right shape for asserting which
/// refusals fire, and it is blind to everything about the git sequence itself: whether porcelain
/// output parses, whether the branch is actually created, whether the commit contains the notes,
/// whether the push has an upstream to push to. The one bug found in this command so far -
/// preparation refusing to continue because of the scaffold it had just written - was invisible
/// to a scripted runner for exactly that reason.
///
/// Only `gh` is faked. It is not what these are testing, and calling it for real would open pull
/// requests on someone's account.
/// </summary>
public sealed class ReleasePreparationOverRealGitTests : IDisposable
{
    private static readonly ReleaseVersion Version = ReleaseVersion.Parse("0.1.36");

    private readonly string _work = Path.Combine(
        Path.GetTempPath(),
        "localai-release-git-" + Guid.NewGuid().ToString("N"));

    private readonly string _repository;
    private readonly StringWriter _output = new();
    private readonly RealGitFakeGh _runner = new();

    public ReleasePreparationOverRealGitTests()
    {
        _repository = Path.Combine(_work, "repository");
        var origin = Path.Combine(_work, "origin.git");
        Directory.CreateDirectory(_repository);
        Directory.CreateDirectory(origin);

        Git(origin, "init", "--bare", "-b", "main");
        Git(_repository, "init", "-b", "main");
        Git(_repository, "config", "user.email", "tests@local.invalid");
        Git(_repository, "config", "user.name", "LocalAi Tests");
        File.WriteAllText(Path.Combine(_repository, "README.md"), "# Fixture\r\n");
        Git(_repository, "add", "README.md");
        Git(_repository, "commit", "-m", "Initial");
        Git(_repository, "remote", "add", "origin", origin);
        Git(_repository, "push", "--set-upstream", "origin", "main");
        Git(_repository, "tag", "0.1.35");
        Git(_repository, "push", "origin", "0.1.35");
    }

    [Fact]
    public async Task A_first_run_writes_the_notes_and_opens_nothing()
    {
        var exitCode = await Prepare();

        Assert.Equal(2, exitCode);
        Assert.True(File.Exists(ReleaseNotes.EnglishPath(_repository, Version)));
        Assert.True(File.Exists(ReleaseNotes.RussianPath(_repository, Version)));
        Assert.DoesNotContain("release/0.1.36", Branches(), StringComparison.Ordinal);
        Assert.Empty(_runner.GhCalls);
    }

    /// <summary>
    /// The run that matters: the scaffold from the first run is sitting uncommitted in the tree,
    /// which is the state that used to stop the command dead.
    /// </summary>
    [Fact]
    public async Task A_second_run_commits_the_written_notes_and_opens_the_pull_request()
    {
        await Prepare();
        WriteRealNotes();

        var exitCode = await Prepare();

        // The command's own output is the failure message: an exit code on its own says a
        // refusal fired without saying which, and there are six of them.
        Assert.True(exitCode == 0, _output.ToString());
        Assert.Contains("release/0.1.36", Branches(), StringComparison.Ordinal);
        Assert.Equal(
            "docs/releases/0.1.36.md\ndocs/releases/0.1.36.ru.md",
            Git(_repository, "show", "--name-only", "--pretty=format:", "HEAD").Trim()
                .Replace("\r\n", "\n"));
        Assert.Contains(
            "release/0.1.36",
            Git(_repository, "branch", "--remotes"),
            StringComparison.Ordinal);
        var call = Assert.Single(_runner.GhCalls);
        Assert.Equal(["pr", "create"], call.Take(2));
        Assert.Contains("release/0.1.36", call);
    }

    [Fact]
    public async Task A_version_that_is_already_released_is_refused_before_anything_is_written()
    {
        var exitCode = await new ReleaseCommand(_runner, _repository, _output)
            .PrepareAsync(
                ReleaseVersion.Parse("0.1.35"),
                TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(ReleaseNotes.EnglishPath(_repository, ReleaseVersion.Parse("0.1.35"))));
        Assert.Empty(_runner.GhCalls);
    }

    /// <summary>
    /// A change that is not this release's notes is the case the clean-tree rule exists for, and
    /// here it is a real modified file reported by real git rather than a line from a table.
    /// </summary>
    [Fact]
    public async Task An_unrelated_change_in_the_tree_stops_preparation()
    {
        File.WriteAllText(Path.Combine(_repository, "README.md"), "# Changed\r\n");

        var exitCode = await Prepare();

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(ReleaseNotes.EnglishPath(_repository, Version)));
        Assert.Contains("README.md", _output.ToString(), StringComparison.Ordinal);
    }

    private Task<int> Prepare() =>
        new ReleaseCommand(_runner, _repository, _output)
            .PrepareAsync(Version, TestContext.Current.CancellationToken);

    private string Branches() => Git(_repository, "branch", "--list");

    private void WriteRealNotes()
    {
        File.WriteAllText(
            ReleaseNotes.EnglishPath(_repository, Version),
            $"# LocalAi {Version}\r\n\r\n[Русская версия]({Version}.ru.md)\r\n\r\nA real change.\r\n");
        File.WriteAllText(
            ReleaseNotes.RussianPath(_repository, Version),
            $"# LocalAi {Version}\r\n\r\n[English version]({Version}.md)\r\n\r\nНастоящее изменение.\r\n");
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }.With(arguments))!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? standardOutput
            : throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited with {process.ExitCode}: {standardError}");
    }

    private sealed class RealGitFakeGh : IProcessRunner
    {
        private readonly SystemProcessRunner _real = new();

        public List<string[]> GhCalls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (string.Equals(executable, "gh", StringComparison.OrdinalIgnoreCase))
            {
                GhCalls.Add([.. arguments]);
                return Task.FromResult(
                    new ProcessResult(0, string.Empty, string.Empty, false, false));
            }

            return _real.RunAsync(executable, arguments, timeout, cancellationToken);
        }
    }

    public void Dispose()
    {
        _output.Dispose();
        if (!Directory.Exists(_work))
        {
            return;
        }

        // Git marks objects read-only, and Directory.Delete refuses those on Windows.
        foreach (var file in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_work, recursive: true);
    }
}

internal static class ProcessStartInfoExtensions
{
    public static System.Diagnostics.ProcessStartInfo With(
        this System.Diagnostics.ProcessStartInfo startInfo,
        IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
