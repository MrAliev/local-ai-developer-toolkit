using System.Security.Cryptography;
using LocalAi.Contracts.Activation;

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
    [InlineData("v1.")]
    [InlineData("v1 ")]
    [InlineData("CON")]
    [InlineData("../v1")]
    public async Task Activate_rejects_unsafe_target_with_sanitized_error(string version)
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v2"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        using var error = new StringWriter();

        var exitCode = await LauncherProgram.RunAsync(
            [
                "activate",
                version,
                "--if-current-sha256",
                Convert.ToHexString(SHA256.HashData(before)),
                "--stop-running",
            ],
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "version_path_invalid: The LocalAi version name is invalid." +
            Environment.NewLine,
            error.ToString());
        Assert.DoesNotContain(version, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("invalid-utf8")]
    [InlineData("schema-overflow")]
    [InlineData("schema-exponent")]
    public async Task Activate_sanitizes_invalid_pointer_without_raw_content_or_path(
        string failure)
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        var bytes = failure switch
        {
            "malformed" => System.Text.Encoding.UTF8.GetBytes("{ raw-secret"),
            "oversized" => Enumerable.Repeat(
                (byte)'X',
                CurrentPointerSnapshot.MaximumBytes + 1).ToArray(),
            "invalid-utf8" => new byte[] { 0xC3, 0x28 },
            "schema-overflow" => System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":2147483648,\"version\":\"v1\"}"),
            "schema-exponent" => System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1e1000,\"version\":\"v1\"}"),
            _ => throw new InvalidOperationException(),
        };
        File.WriteAllBytes(install.CurrentPath, bytes);
        using var error = new StringWriter();

        var exitCode = await LauncherProgram.RunAsync(
            [
                "activate",
                "v2",
                "--if-current-sha256",
                Convert.ToHexString(SHA256.HashData(bytes)),
            ],
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "current_pointer_invalid: The LocalAi current-version pointer is invalid." +
            Environment.NewLine,
            error.ToString());
        Assert.DoesNotContain(install.CurrentPath, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-secret", error.ToString(), StringComparison.Ordinal);
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
