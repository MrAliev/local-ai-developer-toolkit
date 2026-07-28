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

        var result = HookInstaller.Install(_root, Path.Combine(_root, "localai.exe"));

        Assert.Equal(4, result.Installed.Count);
        Assert.Single(result.Chained);
        Assert.True(File.Exists(Path.Combine(hooks, "post-commit.pre-localai")));
        var dispatcher = File.ReadAllText(Path.Combine(hooks, "post-commit"));
        Assert.Contains("LocalAi managed dispatcher", dispatcher);
        Assert.Contains("post-commit.pre-localai", dispatcher);
        Assert.Contains("localai.exe\" hook post-commit", dispatcher);
    }

    [Fact]
    public void Reinstall_is_idempotent()
    {
        HookInstaller.Install(_root, Path.Combine(_root, "localai.exe"));

        var second = HookInstaller.Install(_root, Path.Combine(_root, "localai.exe"));

        Assert.Empty(second.Chained);
        Assert.Equal(4, second.Installed.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
