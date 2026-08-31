using CodeSearch.Mcp;
using System.Diagnostics;
using System.Reflection;

namespace CodeSearch.Tests;

/// <summary>
/// The child-process contract behind index_refresh: cancelling the tool call must cancel
/// the work, not orphan it. A sync left running after "cancellation" keeps writing the
/// shared index, and the retry the caller sends next races it (#198).
/// </summary>
public sealed class RunProcessToCompletionTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(),
        "codesearch-run-process-" + Guid.NewGuid().ToString("N"));

    public RunProcessToCompletionTests()
    {
        Directory.CreateDirectory(_work);
    }

    [Fact]
    public async Task Cancellation_kills_the_child_and_still_observes_both_pipes()
    {
        var pidPath = Path.Combine(_work, "child.pid");
        // The sleep is far longer than any run of this test: the child must never exit on
        // its own, so the only way it can be gone afterwards is the kill under test.
        var start = Start("write-pid-then-sleep", pidPath, "600");
        using var cancellation = new CancellationTokenSource();

        var run = CodeSearchTools.RunProcessToCompletionAsync(start, cancellation.Token);
        var pid = await ReadPidWhenWrittenAsync(pidPath);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await WaitUntilGoneAsync(pid);
    }

    [Fact]
    public async Task Large_output_on_both_pipes_is_read_concurrently_to_completion()
    {
        // Both writes are larger than any sane pipe buffer: a parent reading the pipes in
        // sequence rather than concurrently deadlocks here (the harness run timeout is what
        // would catch that), and one draining them after exit truncates.
        var start = Start("write", "200000", "100000");

        var result = await CodeSearchTools.RunProcessToCompletionAsync(
            start,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(200_000, result.Output.Length);
        Assert.Equal(100_000, result.Error.Length);
    }

    private static ProcessStartInfo Start(params string[] arguments)
    {
        var start = new ProcessStartInfo(Fixture)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>
    /// Waits until the child has written a complete pid, rather than assuming a file that
    /// exists already has content: reading too early yields an empty string and a parse
    /// failure that looks nothing like the race that caused it.
    /// </summary>
    private static async Task<int> ReadPidWhenWrittenAsync(string pidPath)
    {
        while (true)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(pidPath))
            {
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(
                        pidPath,
                        TestContext.Current.CancellationToken);
                }
                catch (IOException)
                {
                    // The child can still hold the file open for its write when the poll
                    // lands between creation and close; a sharing violation here is the
                    // same "not written yet" condition this loop already waits out, not a
                    // failure. Seen as a one-off on a CI runner where exactly that
                    // interleaving happened.
                    text = string.Empty;
                }

                if (int.TryParse(text, out var pid))
                {
                    return pid;
                }
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Waits for the condition — the process being gone — rather than for a duration. A
    /// kill that never lands leaves this loop to the harness run timeout, where a hang
    /// belongs.
    /// </summary>
    private static async Task WaitUntilGoneAsync(int pid)
    {
        while (true)
        {
            try
            {
                using var child = Process.GetProcessById(pid);
                if (child.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private static string Fixture
    {
        get
        {
            var path = typeof(RunProcessToCompletionTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "ProcessFixtureExecutable")
                .Value!;
            Assert.True(
                File.Exists(path),
                $"The process fixture was not built at '{path}'.");
            return path;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
