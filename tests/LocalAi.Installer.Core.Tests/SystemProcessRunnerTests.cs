using System.Text.Json;
using System.Diagnostics;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Tests;

public sealed class SystemProcessRunnerTests
{
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
                TimeSpan.FromSeconds(10),
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
                TimeSpan.FromSeconds(10),
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
        var runner = new SystemProcessRunner();
        var pidPath = TemporaryPath(".pid");
        var scriptPath = TemporaryPath(".ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "Set-Content -LiteralPath $args[0] -Value $PID; Start-Sleep -Seconds 30",
            TestContext.Current.CancellationToken);
        try
        {
            // The script sleeps for 30 seconds, so any budget below that still times out. Two
            // seconds also had to cover PowerShell's startup before the script recorded its pid;
            // under a full parallel test run it did not, and the test failed on a missing pid
            // file instead of on the classification it exists to check.
            var result = await runner.RunAsync(
                ResolvePowerShell(),
                PowerShellFileArguments(scriptPath, pidPath),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            Assert.True(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Null(result.ExitCode);
            AssertProcessExited(int.Parse(await File.ReadAllTextAsync(
                pidPath,
                TestContext.Current.CancellationToken)));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(pidPath);
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
                TimeSpan.FromSeconds(10),
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
