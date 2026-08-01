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
            var result = await runner.RunAsync(
                ResolvePowerShell(),
                PowerShellFileArguments(scriptPath, pidPath),
                TimeSpan.FromSeconds(2),
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
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var result = await runner.RunAsync(
                ResolvePowerShell(),
                PowerShellFileArguments(scriptPath, pidPath),
                TimeSpan.FromSeconds(30),
                cancellation.Token);

            Assert.False(result.TimedOut);
            Assert.True(result.Cancelled);
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
