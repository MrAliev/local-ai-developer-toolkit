using System.Security.Cryptography;
using LocalAi.Contracts.Activation;

namespace LocalAi.Launcher.Tests;

public sealed class ToolRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-tool-runner-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Forwards_arguments_environment_and_exit_code_to_real_child()
    {
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "version.txt");
        var command = $"echo %LOCALAI_ACTIVE_VERSION%>{output} & exit /b 17";
        var runner = new ToolRunner(@"C:\LocalAi\bin\launcher\localai-launcher.exe");

        var exitCode = await runner.RunAsync(
            Environment.GetEnvironmentVariable("ComSpec")!,
            ["/d", "/c", command],
            "v1",
            TestContext.Current.CancellationToken);

        Assert.Equal(17, exitCode);
        Assert.Equal("v1", File.ReadAllText(output).Trim());
    }

    [Fact]
    public async Task Proxies_real_child_stdout_and_stderr()
    {
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        var runner = new ToolRunner(
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            standardOutput,
            standardError);

        var exitCode = await runner.RunAsync(
            Environment.GetEnvironmentVariable("ComSpec")!,
            ["/d", "/c", "echo child-out & echo child-error 1>&2"],
            "v1",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "child-out",
            System.Text.Encoding.UTF8.GetString(standardOutput.ToArray()),
            StringComparison.Ordinal);
        Assert.Contains(
            "child-error",
            System.Text.Encoding.UTF8.GetString(standardError.ToArray()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxies_real_child_stdin()
    {
        using var standardInput = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("hello-from-launcher\r\n"));
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        var runner = new ToolRunner(
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            standardInput,
            standardOutput,
            standardError);

        var exitCode = await runner.RunAsync(
            Environment.GetEnvironmentVariable("ComSpec")!,
            ["/d", "/v:on", "/c", "set /p line=& echo INPUT:!line!"],
            "v1",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "INPUT:hello-from-launcher",
            System.Text.Encoding.UTF8.GetString(standardOutput.ToArray()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Application_holds_shared_lease_until_real_child_exits()
    {
        using var install = TestInstall.CreateComplete("v1");
        install.ReplaceTool(
            "v1",
            "localai.exe",
            Environment.GetEnvironmentVariable("ComSpec")!);
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var signal = Path.Combine(install.Root, "child-started.txt");
        var command = $"echo started>{signal} & ping -n 3 127.0.0.1 > nul";
        var application = new LauncherApplication(
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe");

        var running = application.RunAsync(
            "localai",
            ["/d", "/c", command],
            TestContext.Current.CancellationToken);
        await WaitForFileAsync(signal, TestContext.Current.CancellationToken);

        var error = Assert.Throws<LauncherException>(
            () => VersionLease.AcquireExclusive(
                Path.Combine(install.BinRoot, "current.lock"),
                TimeSpan.Zero));
        Assert.Equal("version_in_use", error.Code);

        Assert.Equal(0, await running);
        using var exclusive = VersionLease.AcquireExclusive(
            Path.Combine(install.BinRoot, "current.lock"),
            TimeSpan.Zero);
        Assert.NotNull(exclusive);
    }

    [Fact]
    public async Task Activation_stops_existing_once_and_blocks_new_tool_start_until_commit()
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.ReplaceTool("v1", "localai.exe", Environment.GetEnvironmentVariable("ComSpec")!);
        install.ReplaceTool("v2", "localai.exe", Environment.GetEnvironmentVariable("ComSpec")!);
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var existingStarted = Path.Combine(install.Root, "existing-started.txt");
        var releaseExisting = Path.Combine(install.Root, "release-existing.txt");
        var newVersion = Path.Combine(install.Root, "new-version.txt");
        var application = new LauncherApplication(
            install.BinRoot,
            @"C:\LocalAi\bin\launcher\localai-launcher.exe");
        var existing = application.RunAsync(
            "localai",
            [
                "/d", "/c",
                $"echo started>{existingStarted} & ping -n 7 127.0.0.1 > nul",
            ],
            TestContext.Current.CancellationToken);
        await WaitForFileAsync(existingStarted, TestContext.Current.CancellationToken);

        var stopCount = 0;
        var stopObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new ProcessSnapshot(
            42,
            DateTimeOffset.UtcNow,
            Path.Combine(install.VersionDirectory("v1"), "localai.exe"),
            null);
        var activator = new VersionActivator(
            install.BinRoot,
            new LocalAiProcessController(
                () => [process],
                (_, _) =>
                {
                    Interlocked.Increment(ref stopCount);
                    File.WriteAllText(releaseExisting, "release");
                    stopObserved.TrySetResult();
                }),
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var before = File.ReadAllBytes(install.CurrentPath);
        var activation = Task.Run(
            () => activator.Activate(
                "v2",
                stopRunning: true,
                CurrentPointerExpectation.ExactSha256(SHA256.HashData(before))),
            TestContext.Current.CancellationToken);
        await stopObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        var newStart = Task.Run(
            () => application.RunAsync(
                "localai",
                ["/d", "/c", $"echo %LOCALAI_ACTIVE_VERSION%>{newVersion}"],
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(newStart.IsCompleted);

        await activation;
        Assert.Equal(0, await existing);
        Assert.Equal(0, await newStart);
        Assert.Equal(1, stopCount);
        Assert.Equal("v2", File.ReadAllText(newVersion).Trim());
        Assert.Equal("v2", new VersionResolver(install.BinRoot).ReadCurrent().Version);
    }

    [Fact]
    public async Task Missing_child_is_reported_with_stable_error_code()
    {
        var runner = new ToolRunner(
            @"C:\LocalAi\bin\launcher\localai-launcher.exe");

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => runner.RunAsync(
                Path.Combine(_root, "missing.exe"),
                [],
                "v1",
                TestContext.Current.CancellationToken));

        Assert.Equal("child_start_failed", error.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Child signal was not created: {path}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }
}
