using System.Text.Json;
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

        var result = await runner.RunAsync(
            ResolvePowerShell(),
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task Classifies_caller_cancellation_separately_from_timeout()
    {
        var runner = new SystemProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var result = await runner.RunAsync(
            ResolvePowerShell(),
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            TimeSpan.FromSeconds(30),
            cancellation.Token);

        Assert.False(result.TimedOut);
        Assert.True(result.Cancelled);
        Assert.Null(result.ExitCode);
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
