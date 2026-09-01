using LocalAi.Cli;
using System.Diagnostics;

namespace LocalAi.IntegrationTests;

/// <summary>
/// `index_refresh` is on the pre-approved tool list because its description promises it refuses
/// to run inline when the work is large and hands back a command to run in the background. No
/// code applied that bound, so a pre-approved call could block for the better part of an hour
/// on a cold build — the exact case the refusal was written for (#275).
///
/// The bound belongs here rather than in the MCP tool: the tool shells out to this command, and
/// a limit the command itself does not honour is a limit that only holds for one caller.
/// </summary>
public sealed class SyncInlineLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-inline-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "localai-inline-runtime-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Over the limit: nothing is built, and the result says how much work was declined so the
    /// caller can say something better than "too big".
    /// </summary>
    [Fact]
    public async Task Work_over_the_inline_limit_is_refused_rather_than_run()
    {
        Repository();

        var result = await CodeSearchSyncCommand.ExecuteAsync(
            _root,
            cancellationToken: TestContext.Current.CancellationToken,
            runtimeRoot: _runtimeRoot,
            refuseInlineOverFiles: 0);

        Assert.NotNull(result.RefusedFiles);
        Assert.True(
            result.RefusedFiles > 0,
            "the refusal should carry the work it declined, not just the fact of declining");
        Assert.False(
            Directory.Exists(Path.Combine(_runtimeRoot, "repositories")) &&
            Directory.EnumerateFiles(_runtimeRoot, "*.cidx", SearchOption.AllDirectories).Any(),
            "a refused run must not leave an index behind");
    }

    /// <summary>
    /// The limit is a ceiling on what may run, so work equal to it is allowed and work one
    /// chunk over it is not. Checked by asking twice: the first call reports what the work
    /// actually is, the second sets the limit one below that.
    ///
    /// The allowed side is deliberately not exercised here — proving it would mean embedding
    /// the repository, which needs a broker this test has no business requiring.
    /// </summary>
    [Fact]
    public async Task The_limit_is_a_ceiling_rather_than_a_threshold()
    {
        Repository();

        var planned = (await CodeSearchSyncCommand.ExecuteAsync(
            _root,
            cancellationToken: TestContext.Current.CancellationToken,
            runtimeRoot: _runtimeRoot,
            refuseInlineOverFiles: 0)).RefusedFiles;
        Assert.NotNull(planned);

        var refused = await CodeSearchSyncCommand.ExecuteAsync(
            _root,
            cancellationToken: TestContext.Current.CancellationToken,
            runtimeRoot: _runtimeRoot,
            refuseInlineOverFiles: planned - 1);

        Assert.Equal(planned, refused.RefusedFiles);
    }

    /// <summary>
    /// The refusal has to come before the semantic phase, not after it. Roslyn loads the whole
    /// solution there and the SCIP adapters shell out per language — the comment beside that
    /// call says "minutes on a large repository" — so refusing afterwards still blocks a
    /// pre-approved tool call for minutes before saying no, which is the harm #275 is about.
    ///
    /// Observed by what the run leaves in staging: the semantic phase writes a .sidx there, so
    /// a refusal that happens first leaves none.
    /// </summary>
    [Fact]
    public async Task The_refusal_comes_before_the_semantic_phase()
    {
        Repository();

        await CodeSearchSyncCommand.ExecuteAsync(
            _root,
            cancellationToken: TestContext.Current.CancellationToken,
            runtimeRoot: _runtimeRoot,
            refuseInlineOverFiles: 0);

        Assert.False(
            Directory.Exists(_runtimeRoot) &&
            Directory.EnumerateFiles(_runtimeRoot, "*.sidx", SearchOption.AllDirectories).Any(),
            "a refused run must not have paid for the semantic phase first");
    }

    private void Repository()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "LocalAi Tests");
        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(
                Path.Combine(_root, $"File{index}.cs"),
                $"public sealed class File{index} {{ public int Value => {index}; }}");
        }

        Git("add", ".");
        Git("commit", "-m", "initial");
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output.Trim();
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _runtimeRoot })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
