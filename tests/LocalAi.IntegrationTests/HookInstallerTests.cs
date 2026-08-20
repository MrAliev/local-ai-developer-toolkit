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
    public void Installs_where_core_hooksPath_points_rather_than_into_the_git_directory()
    {
        var common = Path.Combine(_root, ".git");
        Directory.CreateDirectory(common);

        var result = HookInstaller.Install(
            common,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"],
            ".githooks",
            _root);

        Assert.Equal(Path.Combine(_root, ".githooks"), result.HooksDirectory);
        Assert.True(File.Exists(Path.Combine(_root, ".githooks", "post-commit")));
        Assert.False(Directory.Exists(Path.Combine(common, "hooks")));
    }

    [Fact]
    public void Installs_beside_the_husky_runner_because_husky_rewrites_it()
    {
        var common = Path.Combine(_root, ".git");
        var runner = Path.Combine(_root, ".husky", "_");
        Directory.CreateDirectory(common);
        Directory.CreateDirectory(runner);
        // `_/h` is what husky's own shims source, and the marker that this directory is the one
        // husky deletes and recreates on every install.
        File.WriteAllText(Path.Combine(runner, "h"), "#!/usr/bin/env sh\n");

        var result = HookInstaller.Install(
            common,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"],
            ".husky/_",
            _root);

        Assert.Equal(Path.Combine(_root, ".husky"), result.HooksDirectory);
        Assert.True(File.Exists(Path.Combine(_root, ".husky", "post-commit")));
        Assert.False(File.Exists(Path.Combine(runner, "post-commit")));
    }

    [Fact]
    public void A_plain_underscore_directory_is_not_mistaken_for_husky()
    {
        var common = Path.Combine(_root, ".git");
        Directory.CreateDirectory(common);

        var result = HookInstaller.Install(
            common,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"],
            "hooks/_",
            _root);

        Assert.Equal(Path.Combine(_root, "hooks", "_"), result.HooksDirectory);
    }

    [Fact]
    public void Dispatchers_written_into_the_working_tree_are_excluded_locally()
    {
        var common = Path.Combine(_root, ".git");
        Directory.CreateDirectory(common);

        var first = HookInstaller.Install(
            common,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"],
            ".husky",
            _root);
        HookInstaller.Install(
            common,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"],
            ".husky",
            _root);

        Assert.True(first.InsideWorkingTree);
        var exclude = File.ReadAllLines(Path.Combine(common, "info", "exclude"));
        Assert.Contains("/.husky/post-commit", exclude);
        Assert.Contains("/.husky/post-commit.pre-localai", exclude);
        Assert.Equal(
            1,
            exclude.Count(line => line == "/.husky/post-commit"));
    }

    [Fact]
    public void Hooks_in_the_git_directory_need_no_exclusion()
    {
        var common = Path.Combine(_root, ".git");
        Directory.CreateDirectory(common);

        var result = HookInstaller.Install(
            common,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"],
            configuredHooksPath: null,
            workingTreeRoot: _root);

        Assert.False(result.InsideWorkingTree);
        Assert.False(File.Exists(Path.Combine(common, "info", "exclude")));
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
