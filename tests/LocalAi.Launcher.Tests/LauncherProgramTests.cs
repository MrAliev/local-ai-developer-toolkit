using System.Security.Cryptography;

namespace LocalAi.Launcher.Tests;

public sealed class LauncherProgramTests
{
    [Fact]
    public async Task Run_command_returns_real_child_exit_code()
    {
        using var install = TestInstall.CreateComplete("v1");
        install.ReplaceTool(
            "v1",
            "localai.exe",
            Environment.GetEnvironmentVariable("ComSpec")!);
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        using var error = new StringWriter();

        var exitCode = await LauncherProgram.RunAsync(
            ["run", "localai", "/d", "/c", "exit /b 23"],
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(23, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Invalid_command_prints_usage_to_stderr()
    {
        using var install = TestInstall.CreateComplete("v1");
        using var error = new StringWriter();

        var exitCode = await LauncherProgram.RunAsync(
            ["unknown"],
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains(
            "Usage: localai-launcher run",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launcher_failure_prints_stable_code_to_stderr()
    {
        using var install = TestInstall.CreateComplete("v1");
        using var error = new StringWriter();

        var exitCode = await LauncherProgram.RunAsync(
            ["run", "localai", "native", "tags"],
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.StartsWith(
            "current_pointer_missing:",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activate_command_commits_requested_version()
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        using var error = new StringWriter();

        var exitCode = await LauncherProgram.RunAsync(
            [
                "activate",
                "v2",
                "--if-current-sha256",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(install.CurrentPath))),
            ],
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("v2", new VersionResolver(install.BinRoot).ReadCurrent().Version);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("activate", "v2")]
    [InlineData("activate", "v2", "--if-current-missing", "--if-current-sha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("activate", "v2", "--if-current-sha256", "aa")]
    [InlineData("activate", "v2", "--if-current-missing", "--if-current-missing")]
    [InlineData("activate", "v2", "--unknown")]
    public async Task Activate_requires_one_strict_CAS_expectation(params string[] arguments)
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        using var error = new StringWriter();

        var exitCode = await LauncherProgram.RunAsync(
            arguments,
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
        Assert.Contains("--if-current", error.ToString(), StringComparison.Ordinal);
    }
}
