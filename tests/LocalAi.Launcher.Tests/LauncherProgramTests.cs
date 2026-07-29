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
}
