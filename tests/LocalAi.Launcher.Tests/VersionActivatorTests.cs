namespace LocalAi.Launcher.Tests;

public sealed class VersionActivatorTests
{
    [Fact]
    public void Incomplete_candidate_leaves_pointer_byte_for_byte_unchanged()
    {
        using var install = TestInstall.CreateComplete("v1");
        install.CreateIncomplete("v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);

        var error = Assert.Throws<LauncherException>(
            () => CreateActivator(install).Activate("v2", stopRunning: false));

        Assert.Equal("version_incomplete", error.Code);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    [Fact]
    public async Task Concurrent_activators_commit_one_complete_pointer()
    {
        using var install = TestInstall.CreateComplete("v1", "v2", "v3");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        await Task.WhenAll(
            Task.Run(
                () => CreateActivator(install).Activate("v2", stopRunning: false),
                TestContext.Current.CancellationToken),
            Task.Run(
                () => CreateActivator(install).Activate("v3", stopRunning: false),
                TestContext.Current.CancellationToken));

        var resolved = new VersionResolver(install.BinRoot).Resolve("localai");
        Assert.Contains(resolved.Version, new[] { "v2", "v3" });
        Assert.Empty(Directory.EnumerateFiles(install.BinRoot, "*.tmp"));
    }

    [Fact]
    public void Active_run_lease_preserves_previous_pointer()
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        using var lease = VersionLease.AcquireShared(
            Path.Combine(install.BinRoot, "current.lock"));

        var error = Assert.Throws<LauncherException>(
            () => CreateActivator(install).Activate("v2", stopRunning: false));

        Assert.Equal("version_in_use", error.Code);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    [Fact]
    public void Active_broker_without_run_lease_preserves_previous_pointer()
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        var broker = new ProcessSnapshot(
            42,
            DateTimeOffset.UtcNow,
            Environment.GetEnvironmentVariable("ComSpec")!,
            Path.Combine(
                install.VersionDirectory("v1"),
                "LocalAi.Broker.dll"));
        var activator = new VersionActivator(
            install.BinRoot,
            new LocalAiProcessController(
                () => [broker],
                static (_, _) => { }),
            TimeSpan.Zero,
            TimeSpan.Zero);

        var error = Assert.Throws<LauncherException>(
            () => activator.Activate("v2", stopRunning: false));

        Assert.Equal("version_in_use", error.Code);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    private static VersionActivator CreateActivator(TestInstall install) =>
        new(
            install.BinRoot,
            new LocalAiProcessController(
                static () => [],
                static (_, _) => { }),
            TimeSpan.Zero,
            TimeSpan.Zero);
}
