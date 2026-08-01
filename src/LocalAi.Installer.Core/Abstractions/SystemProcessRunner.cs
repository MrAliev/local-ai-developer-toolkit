using System.Diagnostics;
using System.Text;

namespace LocalAi.Installer.Core.Abstractions;

public sealed class SystemProcessRunner : IProcessRunner
{
    private const int DefaultMaximumCapturedCharacters = 65_536;
    private static readonly TimeSpan DefaultTerminationGrace = TimeSpan.FromSeconds(5);
    private readonly IProcessFactory _processFactory;
    private readonly TimeSpan _terminationGrace;
    private readonly int _maximumCapturedCharacters;

    public SystemProcessRunner()
        : this(
            new SystemProcessFactory(),
            DefaultTerminationGrace,
            DefaultMaximumCapturedCharacters)
    {
    }

    public SystemProcessRunner(
        IProcessFactory processFactory,
        TimeSpan terminationGrace,
        int maximumCapturedCharacters = DefaultMaximumCapturedCharacters)
    {
        _processFactory = processFactory
            ?? throw new ArgumentNullException(nameof(processFactory));
        if (terminationGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationGrace));
        }

        if (maximumCapturedCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCapturedCharacters));
        }

        _terminationGrace = terminationGrace;
        _maximumCapturedCharacters = maximumCapturedCharacters;
    }

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ProcessResult(null, string.Empty, string.Empty, false, true);
        }

        var startInfo = CreateStartInfo(executable, arguments);
        using var process = _processFactory.Start(startInfo);
        using var drainCancellation = new CancellationTokenSource();
        var standardOutput = DrainBoundedAsync(
            process.StandardOutput,
            drainCancellation.Token);
        var standardError = DrainBoundedAsync(
            process.StandardError,
            drainCancellation.Token);
        var processExit = process.WaitForExitAsync(CancellationToken.None);
        var timeoutElapsed = Task.Delay(timeout, CancellationToken.None);
        var callerCancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        await Task.WhenAny(processExit, callerCancelled, timeoutElapsed)
            .ConfigureAwait(false);

        if (process.HasExited)
        {
            await processExit.ConfigureAwait(false);
            return await CreateExitResultAsync(
                    process,
                    standardOutput,
                    standardError)
                .ConfigureAwait(false);
        }

        var cause = cancellationToken.IsCancellationRequested
            ? ProcessTerminationCause.Cancellation
            : ProcessTerminationCause.Timeout;
        try
        {
            process.KillTree();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            if (process.HasExited)
            {
                await processExit.ConfigureAwait(false);
                return await CreateExitResultAsync(
                        process,
                        standardOutput,
                        standardError)
                    .ConfigureAwait(false);
            }

            await CancelDrainsAsync(
                    drainCancellation,
                    standardOutput,
                    standardError)
                .ConfigureAwait(false);
            throw new ProcessTerminationException(
                process.Id,
                cause,
                $"Failed to terminate process tree {process.Id}.",
                exception);
        }

        using var terminationCancellation =
            new CancellationTokenSource(_terminationGrace);
        try
        {
            await process.WaitForExitAsync(terminationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (terminationCancellation.IsCancellationRequested)
        {
            await CancelDrainsAsync(
                    drainCancellation,
                    standardOutput,
                    standardError)
                .ConfigureAwait(false);
            throw new ProcessTerminationException(
                process.Id,
                cause,
                $"Process tree {process.Id} did not exit within {_terminationGrace}.");
        }

        if (!process.HasExited)
        {
            await CancelDrainsAsync(
                    drainCancellation,
                    standardOutput,
                    standardError)
                .ConfigureAwait(false);
            throw new ProcessTerminationException(
                process.Id,
                cause,
                $"Process tree {process.Id} did not confirm exit.");
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        return new ProcessResult(
            null,
            output.Text,
            error.Text,
            cause == ProcessTerminationCause.Timeout,
            cause == ProcessTerminationCause.Cancellation,
            output.Truncated,
            error.Truncated);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task<CapturedText> DrainBoundedAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var captured = new StringBuilder(
            Math.Min(_maximumCapturedCharacters, 4_096));
        var buffer = new char[4_096];
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var remaining = _maximumCapturedCharacters - captured.Length;
                if (remaining > 0)
                {
                    captured.Append(buffer, 0, Math.Min(read, remaining));
                }

                truncated |= read > remaining;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return new CapturedText(captured.ToString(), truncated);
    }

    private static async Task<ProcessResult> CreateExitResultAsync(
        IRunningProcess process,
        Task<CapturedText> standardOutput,
        Task<CapturedText> standardError)
    {
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            output.Text,
            error.Text,
            false,
            false,
            output.Truncated,
            error.Truncated);
    }

    private static async Task CancelDrainsAsync(
        CancellationTokenSource drainCancellation,
        Task<CapturedText> standardOutput,
        Task<CapturedText> standardError)
    {
        drainCancellation.Cancel();
        try
        {
            await Task.WhenAll(standardOutput, standardError)
                .WaitAsync(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveFault(standardOutput);
            ObserveFault(standardError);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or
            ObjectDisposedException)
        {
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed record CapturedText(string Text, bool Truncated);
}
