using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Tests;

public sealed class SystemProcessRunnerTests
{
    /// <summary>
    /// The budget a test passes when it expects the child to finish.
    /// </summary>
    /// <remarks>
    /// Thirty seconds is a bound on a child that starts in milliseconds and does one thing, so it
    /// is an assertion that the run finished rather than a bet on how quick the machine is. That
    /// distinction is the whole reason these tests no longer launch PowerShell: an interpreter
    /// that can take ten seconds to reach its first line on a loaded runner turned every budget
    /// here into a coin flip, and one of them came up tails — the run returned no exit code and
    /// the failure read as a broken argument list.
    ///
    /// A hung child is caught here, and by the CI run timeout if it ever escapes this.
    /// </remarks>
    private static readonly TimeSpan Completion = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A model download reports for minutes and finishes once, so a caller that only learns
    /// at the end learns nothing it could have shown. What the reader is handed, the result
    /// still holds: this is an additional view of the same bytes, not a diversion of them.
    /// </summary>
    [Fact]
    public async Task Standard_error_lines_reach_a_reader_and_the_result_keeps_them_too()
    {
        var runner = new SystemProcessRunner();
        var lines = new List<string>();

        var result = await runner.RunAsync(
            Fixture,
            ["error-lines", "3"],
            Completion,
            line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            },
            TestContext.Current.CancellationToken);

        AssertCompleted(result);
        Assert.Equal(["line 1", "line 2", "line 3"], lines);
        Assert.Contains("line 3", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preserves_arguments_without_building_a_shell_command()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            Fixture,
            ["echo-args", "alpha beta", "semi;colon", "\"quoted\""],
            Completion,
            TestContext.Current.CancellationToken);

        AssertCompleted(result);
        var values = JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim());
        Assert.Equal(new[] { "alpha beta", "semi;colon", "\"quoted\"" }, values);
    }

    [Fact]
    public async Task Runs_Windows_command_scripts_without_losing_safe_arguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The one case that cannot use the fixture: what it exercises is the branch that hands a
        // `.cmd` to the command interpreter, so the child has to be a command script.
        var runner = new SystemProcessRunner();
        var scriptPath = TemporaryPath(".cmd");
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\necho %~1^|%~2",
            TestContext.Current.CancellationToken);
        try
        {
            var result = await runner.RunAsync(
                scriptPath,
                ["alpha beta", "semi;colon"],
                Completion,
                TestContext.Current.CancellationToken);

            AssertCompleted(result);
            Assert.Equal("alpha beta|semi;colon", result.StandardOutput.Trim());
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task Rejects_unsafe_Windows_command_script_arguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // No process is started, so the budget passed here is never consulted.
        var runner = new SystemProcessRunner();
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            @"C:\Tools\npm.cmd",
            ["value%PATH%"],
            Completion,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Classifies_timeout_and_kills_only_the_started_process_tree()
    {
        var started = new StartedProcessRecorder();
        var runner = new SystemProcessRunner(started, TimeSpan.FromSeconds(5));

        // The child sleeps for half a minute against a two-second budget. Unlike the cases above,
        // a slow start cannot break this one: it only makes the process more certainly alive when
        // the budget expires, never less.
        var result = await runner.RunAsync(
            Fixture,
            ["sleep", "30"],
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Null(result.ExitCode);
        AssertProcessExited(started.ProcessId);
    }

    [Fact]
    public async Task Classifies_caller_cancellation_separately_from_timeout()
    {
        var runner = new SystemProcessRunner();
        var pidPath = TemporaryPath(".pid");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var run = runner.RunAsync(
                Fixture,
                ["write-pid-then-sleep", pidPath, "30"],
                Completion,
                cancellation.Token);

            // Cancel once the child has actually recorded its pid, not after a fixed delay: a
            // fixed delay is a guess about startup, and this test is about classification.
            var pid = await ReadPidWhenWrittenAsync(
                pidPath,
                TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            var result = await run;

            Assert.False(result.TimedOut);
            Assert.True(result.Cancelled);
            Assert.Null(result.ExitCode);
            AssertProcessExited(pid);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task Bounds_output_while_draining_both_redirected_pipes()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            Fixture,
            ["write", "70000", "70000"],
            Completion,
            TestContext.Current.CancellationToken);

        AssertCompleted(result);
        Assert.Equal(65_536, result.StandardOutput.Length);
        Assert.Equal(65_536, result.StandardError.Length);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task Streams_binary_standard_output_to_a_file_without_text_decoding()
    {
        var runner = new SystemProcessRunner();
        var outputPath = TemporaryPath(".bin");
        try
        {
            var result = await runner.RunToFileAsync(
                Fixture,
                ["write-binary", "0", "255", "1", "254", "2"],
                outputPath,
                Completion,
                TestContext.Current.CancellationToken);

            AssertCompleted(result);
            Assert.Equal(
                new byte[] { 0, 255, 1, 254, 2 },
                await File.ReadAllBytesAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Classifies_a_binary_output_timeout_and_kills_the_started_process_tree()
    {
        var started = new StartedProcessRecorder();
        var runner = new SystemProcessRunner(started, TimeSpan.FromSeconds(5));
        var outputPath = TemporaryPath(".bin");
        try
        {
            var result = await runner.RunToFileAsync(
                Fixture,
                ["sleep", "30"],
                outputPath,
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);

            Assert.True(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Null(result.ExitCode);
            AssertProcessExited(started.ProcessId);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <summary>
    /// Asserts that the child ran to completion, and says which of the two ways it failed to.
    /// </summary>
    /// <remarks>
    /// Written because the difference matters to whoever reads the failure. Asserting the exit
    /// code directly reports "expected 0, got null", which reads as a fault in what was being
    /// tested; the run having been killed on its budget or cancelled is a fact about the machine
    /// or the test, and it should not cost anyone an investigation into argument quoting to
    /// discover that.
    /// </remarks>
    private static void AssertCompleted(ProcessResult result)
    {
        Assert.False(
            result.TimedOut,
            "The child did not finish within its budget. Nothing here measures speed, so this " +
            "is a stuck or starved process rather than a failure of the behaviour under test.");
        Assert.False(
            result.Cancelled,
            "The run was cancelled by its caller, which no test in this file asks for except " +
            "the cancellation case.");
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// The child process these tests start, run out of its own output directory.
    /// </summary>
    /// <remarks>
    /// Its path is baked in at build time by the test project, which also carries the project
    /// reference that guarantees it exists by the time anything here runs.
    /// </remarks>
    private static string Fixture
    {
        get
        {
            var path = typeof(SystemProcessRunnerTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "ProcessFixtureExecutable")
                .Value!;
            Assert.True(
                File.Exists(path),
                $"The process fixture was not built at '{path}'.");
            return path;
        }
    }

    /// <summary>
    /// Waits until the child has written a complete pid, rather than assuming a file that exists
    /// already has content in it: reading too early yields an empty string and a parse failure
    /// that looks nothing like the race that caused it.
    /// </summary>
    private static async Task<int> ReadPidWhenWrittenAsync(
        string pidPath,
        CancellationToken cancellationToken)
    {
        var deadline = TimeSpan.FromSeconds(20);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(pidPath))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(pidPath, cancellationToken);
                    if (int.TryParse(text.Trim(), out var pid))
                    {
                        return pid;
                    }
                }
                catch (IOException)
                {
                    // Still being written; fall through and retry.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException($"The child never recorded a pid in '{pidPath}'.");
    }

    private static string TemporaryPath(string extension) =>
        Path.Combine(
            Path.GetTempPath(),
            $"localai-process-{Guid.NewGuid():N}{extension}");

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"Process {processId} is still running.");
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>
    /// Remembers which process the runner started, at the moment it started it.
    ///
    /// The timeout test used to learn that from a pid the child wrote to a file, which quietly
    /// turned its budget into a bet on how long the child takes to reach its first statement.
    /// The bet was lost on a loaded CI runner — the budget expired while PowerShell was still
    /// starting, the file never appeared, and the test failed on a missing pid rather than on the
    /// classification it exists to check. The pid was available from the process handle all along.
    /// </summary>
    private sealed class StartedProcessRecorder : IProcessFactory
    {
        private readonly SystemProcessFactory _inner = new();

        public int ProcessId { get; private set; }

        public IRunningProcess Start(ProcessStartInfo startInfo)
        {
            var process = _inner.Start(startInfo);
            ProcessId = process.Id;
            return process;
        }
    }
}
