using System.Diagnostics;

namespace LocalAi.Installer.Core.Abstractions;

public sealed class SystemProcessRunner : IProcessRunner
{
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

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var processExit = process.WaitForExitAsync(CancellationToken.None);
        var timeoutElapsed = Task.Delay(timeout, CancellationToken.None);
        var callerCancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(
                processExit,
                timeoutElapsed,
                callerCancelled)
            .ConfigureAwait(false);
        if (completed == processExit)
        {
            await processExit.ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false),
                false,
                false);
        }

        var cancelled = completed == callerCancelled;
        TryKillProcessTree(process);
        await WaitAfterKillAsync(process).ConfigureAwait(false);
        return new ProcessResult(
            null,
            await ReadCompletedOrEmptyAsync(standardOutput).ConfigureAwait(false),
            await ReadCompletedOrEmptyAsync(standardError).ConfigureAwait(false),
            !cancelled,
            cancelled);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            using var waitCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(waitCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<string> ReadCompletedOrEmptyAsync(Task<string> readTask)
    {
        var completed = await Task.WhenAny(
                readTask,
                Task.Delay(TimeSpan.FromSeconds(1)))
            .ConfigureAwait(false);
        return completed == readTask
            ? await readTask.ConfigureAwait(false)
            : string.Empty;
    }
}
