using System.Diagnostics;
using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

public sealed class HookInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-hooks-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Installs_shared_dispatchers_and_chains_existing_hook()
    {
        var hooks = Path.Combine(_root, "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "post-commit"), "#!/bin/sh\necho existing\n");

        var result = HookInstaller.Install(
            _root,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"]);

        Assert.Equal(4, result.Installed.Count);
        Assert.Single(result.Chained);
        Assert.True(File.Exists(Path.Combine(hooks, "post-commit.pre-localai")));
        var dispatcher = File.ReadAllText(Path.Combine(hooks, "post-commit"));
        Assert.Contains("LocalAi managed dispatcher", dispatcher);
        Assert.Contains("post-commit.pre-localai", dispatcher);
        Assert.Contains(
            "localai-launcher.exe\" run localai hook post-commit",
            dispatcher);
    }

    [Fact]
    public void Reinstall_is_idempotent()
    {
        HookInstaller.Install(
            _root,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"]);

        var second = HookInstaller.Install(
            _root,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"]);

        Assert.Empty(second.Chained);
        Assert.Equal(4, second.Installed.Count);
    }

    [Fact]
    public void Missing_launcher_path_is_rejected_before_hooks_are_created()
    {
        var error = Assert.Throws<ArgumentException>(
            () => HookInstaller.Install(
                _root,
                string.Empty,
                ["run", "localai"]));

        Assert.Contains("launcher", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "hooks")));
    }

    [Fact]
    public async Task Direct_cli_invocation_without_launcher_environment_is_rejected()
    {
        Directory.CreateDirectory(_root);
        var startInfo = new ProcessStartInfo(
            "dotnet",
            $"\"{typeof(HookInstaller).Assembly.Location}\" hooks install " +
            $"--root \"{_root}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment.Remove("LOCALAI_LAUNCHER_PATH");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start LocalAi CLI.");

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("LOCALAI_LAUNCHER_PATH", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "hooks")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
