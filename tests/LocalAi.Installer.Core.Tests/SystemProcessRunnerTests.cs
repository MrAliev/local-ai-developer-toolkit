using System.Text.Json;
using System.Diagnostics;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Tests;

public sealed class SystemProcessRunnerTests
{
    /// <summary>
    /// The budget a test passes when it expects the child to finish.
    /// </summary>
    /// <remarks>
    /// It is there to stop a hung process, not to time how fast PowerShell starts. Ten seconds
    /// looked generous until a loaded CI runner spent all of them before the script ran its first
    /// line: the run came back with no exit code, and the failure read as a broken argument list
    /// rather than as a slow machine. Nothing here measures speed, so the budget can be far larger
    /// than any honest run needs — a genuinely stuck child still fails the test, just later.
    /// </remarks>
    private static readonly TimeSpan Completion = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Preserves_arguments_without_building_a_shell_command()
    {
        var runner = new SystemProcessRunner();
        var executable = ResolvePowerShell();
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"localai-process-arguments-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "$args | ConvertTo-Json -Compress",
            TestContext.Current.CancellationToken);
        try
        {
            var arguments = new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
                "alpha beta",
                "semi;colon",
                "\"quoted\"",
            };

            var result = await runner.RunAsync(
                executable,
                arguments,
                Completion,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.False(result.Cancelled);
            var values = JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim());
            Assert.Equal(new[] { "alpha beta", "semi;colon", "\"quoted\"" }, values);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task Runs_Windows_command_scripts_without_losing_safe_arguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

            Assert.Equal(0, result.ExitCode);
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

        var runner = new SystemProcessRunner();
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            @"C:\Tools\npm.cmd",
            ["value%PATH%"],
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Classifies_timeout_and_kills_only_the_started_process_tree()
    {
        var started = new StartedProcessRecorder();
        var runner = new SystemProcessRunner(started, TimeSpan.FromSeconds(5));
        var scriptPath = TemporaryPath(".ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "Start-Sleep -Seconds 30",
            TestContext.Current.CancellationToken);
        try
        {
            // The script sleeps for half a minute, so a ten-second budget times out however
            // slowly PowerShell gets going: a late start only makes the process more certainly
            // alive when the budget expires, never less.
            var result = await runner.RunAsync(
                ResolvePowerShell(),
                PowerShellFileArguments(scriptPath),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            Assert.True(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Null(result.ExitCode);
            AssertProcessExited(started.ProcessId);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task Classifies_caller_cancellation_separately_from_timeout()
    {
        var runner = new SystemProcessRunner();
        var pidPath = TemporaryPath(".pid");
        var scriptPath = TemporaryPath(".ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "Set-Content -LiteralPath $args[0] -Value $PID; Start-Sleep -Seconds 30",
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var run = runner.RunAsync(
                ResolvePowerShell(),
                PowerShellFileArguments(scriptPath, pidPath),
                TimeSpan.FromSeconds(30),
                cancellation.Token);

            // Cancel once the child has actually recorded its pid, not after a fixed delay. Two
            // seconds raced PowerShell's startup: on a loaded machine the run was cancelled
            // before the script wrote anything, and the test then failed on a missing pid file
            // rather than on the classification it exists to check.
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
            File.Delete(scriptPath);
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task Bounds_output_while_draining_both_redirected_pipes()
    {
        var runner = new SystemProcessRunner();
        var scriptPath = TemporaryPath(".ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "[Console]::Out.Write('o' * 70000); [Console]::Error.Write('e' * 70000)",
            TestContext.Current.CancellationToken);
        try
        {
            var result = await runner.RunAsync(
                ResolvePowerShell(),
                PowerShellFileArguments(scriptPath),
                Completion,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(65_536, result.StandardOutput.Length);
            Assert.Equal(65_536, result.StandardError.Length);
            Assert.True(result.StandardOutputTruncated);
            Assert.True(result.StandardErrorTruncated);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task Streams_binary_standard_output_to_a_file_without_text_decoding()
    {
        var runner = new SystemProcessRunner();
        var scriptPath = TemporaryPath(".ps1");
        var outputPath = TemporaryPath(".bin");
        await File.WriteAllTextAsync(
            scriptPath,
            "$bytes = [byte[]](0,255,1,254,2); " +
            "$stream = [Console]::OpenStandardOutput(); " +
            "$stream.Write($bytes, 0, $bytes.Length); $stream.Flush()",
            TestContext.Current.CancellationToken);
        try
        {
            var result = await runner.RunToFileAsync(
                ResolvePowerShell(),
                PowerShellFileArguments(scriptPath),
                outputPath,
                Completion,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                new byte[] { 0, 255, 1, 254, 2 },
                await File.ReadAllBytesAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Classifies_a_binary_output_timeout_and_kills_the_started_process_tree()
    {
        var started = new StartedProcessRecorder();
        var runner = new SystemProcessRunner(started, TimeSpan.FromSeconds(5));
        var scriptPath = TemporaryPath(".ps1");
        var outputPath = TemporaryPath(".bin");
        await File.WriteAllTextAsync(
            scriptPath,
            "Start-Sleep -Seconds 30",
            TestContext.Current.CancellationToken);
        try
        {
            var result = await runner.RunToFileAsync(
                ResolvePowerShell(),
                PowerShellFileArguments(scriptPath),
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
            File.Delete(scriptPath);
            File.Delete(outputPath);
        }
    }

    private static string[] PowerShellFileArguments(
        string scriptPath,
        params string[] arguments) =>
        [
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            .. arguments,
        ];

    /// <summary>
    /// Waits until the child has written a complete pid. Set-Content creates the file before it
    /// has content, so existence alone is not enough - reading too early yields an empty string
    /// and a parse failure that looks nothing like the race that caused it.
    /// </summary>
    private static async Task<int> ReadPidWhenWrittenAsync(
        string pidPath,
        CancellationToken cancellationToken)
    {
        var deadline = TimeSpan.FromSeconds(30);
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
    /// turned its budget into a bet on how long PowerShell takes to reach its first statement.
    /// The bet was lost on a loaded CI runner — the ten seconds expired while PowerShell was
    /// still starting, the file never appeared, and the test failed on a missing pid rather than
    /// on the classification it exists to check. Raising the budget was the wrong lever twice
    /// over: it cannot be raised past the child's own sleep, and the pid was available from the
    /// process handle all along.
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

    private static string ResolvePowerShell()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(
            Directory.GetParent(systemDirectory)!.FullName,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
    }
}
