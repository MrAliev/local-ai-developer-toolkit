using System.Diagnostics;
using System.Security.Cryptography;
using LocalAi.Contracts.Activation;

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
            () => CreateActivator(install).Activate(
                "v2",
                stopRunning: false,
                ExpectCurrent(before)));

        Assert.Equal("version_incomplete", error.Code);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    [Fact]
    public async Task Concurrent_activators_commit_one_complete_pointer()
    {
        using var install = TestInstall.CreateComplete("v1", "v2", "v3");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var expectation = ExpectCurrent(File.ReadAllBytes(install.CurrentPath));

        var errors = await Task.WhenAll(
            Task.Run(
                () => Record.Exception(() =>
                    CreateActivator(install, TimeSpan.FromSeconds(1))
                        .Activate("v2", false, expectation)),
                TestContext.Current.CancellationToken),
            Task.Run(
                () => Record.Exception(() =>
                    CreateActivator(install, TimeSpan.FromSeconds(1))
                        .Activate("v3", false, expectation)),
                TestContext.Current.CancellationToken));

        var resolved = new VersionResolver(install.BinRoot).Resolve("localai");
        Assert.Contains(resolved.Version, new[] { "v2", "v3" });
        Assert.Single(errors, error => error is null);
        var rejected = Assert.Single(errors, error => error is not null);
        Assert.Equal("current_pointer_changed", Assert.IsType<LauncherException>(rejected).Code);
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
            () => CreateActivator(install).Activate(
                "v2",
                stopRunning: false,
                ExpectCurrent(before)));

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
            () => activator.Activate(
                "v2",
                stopRunning: false,
                ExpectCurrent(before)));

        Assert.Equal("version_in_use", error.Code);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    [Fact]
    public void Stop_running_stops_each_owned_process_exactly_once()
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        var process = new ProcessSnapshot(
            42,
            DateTimeOffset.UtcNow,
            Path.Combine(install.VersionDirectory("v1"), "localai.exe"),
            null);
        var stopCount = 0;
        var activator = new VersionActivator(
            install.BinRoot,
            new LocalAiProcessController(
                () => [process],
                (_, _) => stopCount++),
            TimeSpan.Zero,
            TimeSpan.Zero);

        activator.Activate("v2", stopRunning: true, ExpectCurrent(before));

        Assert.Equal(1, stopCount);
        Assert.Equal("v2", new VersionResolver(install.BinRoot).ReadCurrent().Version);
    }

    [Theory]
    [InlineData("v1.")]
    [InlineData("v1 ")]
    [InlineData("CON")]
    [InlineData("../v1")]
    public async Task Unsafe_target_is_rejected_before_lease_or_process_stop(string version)
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v2"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        var stopCount = 0;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(
            () =>
            {
                using var gate = ActivationCoordinator.AcquireStartupGate(
                    install.BinRoot,
                    TimeSpan.FromSeconds(1));
                held.Set();
                release.Wait(TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);
        held.Wait(TestContext.Current.CancellationToken);
        var activator = new VersionActivator(
            install.BinRoot,
            new LocalAiProcessController(
                static () => [],
                (_, _) => stopCount++),
            TimeSpan.Zero,
            TimeSpan.Zero);

        LauncherException error;
        try
        {
            error = Assert.Throws<LauncherException>(() =>
                activator.Activate(
                    version,
                    stopRunning: true,
                    ExpectCurrent(before)));
        }
        finally
        {
            release.Set();
            await holder;
        }

        Assert.Equal("version_path_invalid", error.Code);
        Assert.Equal("The LocalAi version name is invalid.", error.Message);
        Assert.Equal(0, stopCount);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    [Fact]
    public async Task Startup_gate_and_current_lock_share_one_lease_timeout()
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        using var shared = VersionLease.AcquireShared(
            Path.Combine(install.BinRoot, "current.lock"));
        using var gate = ActivationCoordinator.AcquireStartupGate(
            install.BinRoot,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        using var started = new ManualResetEventSlim();
        var activation = Task.Run(() =>
        {
            started.Set();
            var elapsed = Stopwatch.StartNew();
            var error = Record.Exception(() =>
                CreateActivator(install, TimeSpan.FromMilliseconds(500)).Activate(
                    "v2",
                    stopRunning: false,
                    ExpectCurrent(before)));
            elapsed.Stop();
            return (error, elapsed.Elapsed);
        });

        started.Wait(TestContext.Current.CancellationToken);
        Thread.Sleep(TimeSpan.FromMilliseconds(250));
        gate.Dispose();
        var result = await activation;

        var launcherError = Assert.IsType<LauncherException>(result.error);
        Assert.Equal("version_in_use", launcherError.Code);
        Assert.InRange(
            result.Elapsed,
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(650));
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    [Fact]
    public async Task Stop_timeout_is_separate_from_shared_lease_acquisition_budget()
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        var process = new ProcessSnapshot(
            42,
            DateTimeOffset.UtcNow,
            Path.Combine(install.VersionDirectory("v1"), "localai.exe"),
            null);
        using var shared = VersionLease.AcquireShared(
            Path.Combine(install.BinRoot, "current.lock"));
        Task? release = null;
        var activator = new VersionActivator(
            install.BinRoot,
            new LocalAiProcessController(
                () => [process],
                (_, _) =>
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(350));
                    release = Task.Run(() =>
                    {
                        Thread.Sleep(TimeSpan.FromMilliseconds(100));
                        shared.Dispose();
                    });
                }),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1));

        activator.Activate("v2", stopRunning: true, ExpectCurrent(before));
        await release!;

        Assert.Equal("v2", new VersionResolver(install.BinRoot).ReadCurrent().Version);
    }

    [Fact]
    public void Missing_expectation_activates_only_when_pointer_is_still_missing()
    {
        using var install = TestInstall.CreateComplete("v1");

        CreateActivator(install).Activate(
            "v1",
            stopRunning: false,
            CurrentPointerExpectation.Missing);

        Assert.Equal("v1", new VersionResolver(install.BinRoot).ReadCurrent().Version);
    }

    [Fact]
    public void Exact_hash_expectation_rejects_same_version_raw_rewrite()
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        var expectation = ExpectCurrent(before);
        install.WriteCurrent("""{ "schemaVersion": 1, "version": "v1" }""");
        var rewritten = File.ReadAllBytes(install.CurrentPath);

        var error = Assert.Throws<LauncherException>(() =>
            CreateActivator(install).Activate("v2", false, expectation));

        Assert.Equal("current_pointer_changed", error.Code);
        Assert.Equal(rewritten, File.ReadAllBytes(install.CurrentPath));
    }

    [Fact]
    public void Exact_hash_expectation_rejects_unrelated_third_pointer()
    {
        using var install = TestInstall.CreateComplete("v1", "v2", "v3");
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var expectation = ExpectCurrent(File.ReadAllBytes(install.CurrentPath));
        install.WriteCurrent("""{"schemaVersion":1,"version":"v3"}""");
        var before = File.ReadAllBytes(install.CurrentPath);

        var error = Assert.Throws<LauncherException>(() =>
            CreateActivator(install).Activate("v2", true, expectation));

        Assert.Equal("current_pointer_changed", error.Code);
        Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
    }

    private static VersionActivator CreateActivator(
        TestInstall install,
        TimeSpan? leaseTimeout = null) =>
        new(
            install.BinRoot,
            new LocalAiProcessController(
                static () => [],
                static (_, _) => { }),
            leaseTimeout ?? TimeSpan.Zero,
            TimeSpan.Zero);

    private static CurrentPointerExpectation ExpectCurrent(byte[] bytes) =>
        CurrentPointerExpectation.ExactSha256(SHA256.HashData(bytes));
}
