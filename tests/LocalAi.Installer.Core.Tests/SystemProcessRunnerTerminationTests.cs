using System.Diagnostics;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Tests;

public sealed class SystemProcessRunnerTerminationTests
{
    [Fact]
    public async Task Throws_when_process_tree_kill_fails_and_process_is_still_running()
    {
        var process = new FakeRunningProcess
        {
            KillException = new System.ComponentModel.Win32Exception("Access denied."),
        };
        var runner = new SystemProcessRunner(
            new FakeProcessFactory(process),
            TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsAsync<ProcessTerminationException>(
            () => runner.RunAsync(
                "controlled.exe",
                [],
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken));

        Assert.Equal(42, error.ProcessId);
        Assert.Equal(ProcessTerminationCause.Timeout, error.Cause);
        Assert.IsType<System.ComponentModel.Win32Exception>(error.InnerException);
    }

    [Fact]
    public async Task Throws_when_killed_process_does_not_confirm_exit_within_grace()
    {
        var process = new FakeRunningProcess();
        var runner = new SystemProcessRunner(
            new FakeProcessFactory(process),
            TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsAsync<ProcessTerminationException>(
            () => runner.RunAsync(
                "controlled.exe",
                [],
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken));

        Assert.Equal(ProcessTerminationCause.Timeout, error.Cause);
        Assert.True(process.KillTreeCalled);
    }

    [Fact]
    public async Task Observed_exit_wins_over_simultaneous_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeRunningProcess
        {
            HasExitedValue = true,
            ExitCodeValue = 7,
            WaitForExit = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
        };
        var runner = new SystemProcessRunner(
            new FakeProcessFactory(process),
            TimeSpan.FromMilliseconds(20));

        var result = await runner.RunAsync(
            "controlled.exe",
            [],
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.False(result.TimedOut);
    }

    private sealed class FakeProcessFactory(IRunningProcess process) : IProcessFactory
    {
        public IRunningProcess Start(ProcessStartInfo startInfo) => process;
    }

    private sealed class FakeRunningProcess : IRunningProcess
    {
        public int Id => 42;
        public bool HasExited => HasExitedValue;
        public bool HasExitedValue { get; set; }
        public int ExitCode => ExitCodeValue;
        public int ExitCodeValue { get; set; }
        public TextReader StandardOutput { get; } = new StringReader(string.Empty);
        public Stream StandardOutputStream { get; } = new MemoryStream();
        public TextReader StandardError { get; } = new StringReader(string.Empty);
        public bool KillTreeCalled { get; private set; }
        public Exception? KillException { get; init; }
        public Func<CancellationToken, Task> WaitForExit { get; init; } =
            cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            WaitForExit(cancellationToken);

        public void KillTree()
        {
            KillTreeCalled = true;
            if (KillException is not null)
            {
                throw KillException;
            }
        }

        public void Dispose()
        {
        }
    }
}
