using System.Diagnostics;
using System.Text;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Abstractions;

public sealed class SystemProcessRunner : IProcessRunner, IProcessFileRunner
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

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunCoreAsync(executable, arguments, timeout, null, cancellationToken);

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string> onStandardErrorLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onStandardErrorLine);
        return RunCoreAsync(
            executable,
            arguments,
            timeout,
            onStandardErrorLine,
            cancellationToken);
    }

    private async Task<ProcessResult> RunCoreAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string>? onStandardErrorLine,
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
            drainCancellation.Token,
            onStandardErrorLine);
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

    public async Task<ProcessResult> RunToFileAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string outputPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ProcessResult(null, string.Empty, string.Empty, false, true);
        }

        await using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 81_920,
            useAsync: true);
        var startInfo = CreateStartInfo(executable, arguments);
        using var process = _processFactory.Start(startInfo);
        using var drainCancellation = new CancellationTokenSource();
        var standardOutput = process.StandardOutputStream.CopyToAsync(
            output,
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
            await standardOutput.ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                string.Empty,
                error.Text,
                false,
                false,
                false,
                error.Truncated);
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
                await standardOutput.ConfigureAwait(false);
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                var error = await standardError.ConfigureAwait(false);
                return new ProcessResult(
                    process.ExitCode,
                    string.Empty,
                    error.Text,
                    false,
                    false,
                    false,
                    error.Truncated);
            }

            await CancelFileDrainsAsync(
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
            await CancelFileDrainsAsync(
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
            await CancelFileDrainsAsync(
                    drainCancellation,
                    standardOutput,
                    standardError)
                .ConfigureAwait(false);
            throw new ProcessTerminationException(
                process.Id,
                cause,
                $"Process tree {process.Id} did not confirm exit.");
        }

        await standardOutput.ConfigureAwait(false);
        await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        var capturedError = await standardError.ConfigureAwait(false);
        return new ProcessResult(
            null,
            string.Empty,
            capturedError.Text,
            cause == ProcessTerminationCause.Timeout,
            cause == ProcessTerminationCause.Cancellation,
            false,
            capturedError.Truncated);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var isCommandScript = OperatingSystem.IsWindows() &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // winget, git and ollama follow the console, and the installer has no console at
            // all — so their messages arrive in the OEM code page, and the wizard shows them.
            StandardOutputEncoding = ChildProcessText.ConsoleEncoding,
            StandardErrorEncoding = ChildProcessText.ConsoleEncoding,
        };
        if (isCommandScript)
        {
            startInfo.Arguments =
                "/d /s /c \"" + CommandScriptInvocation(executable, arguments) + "\"";
            return startInfo;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string CommandScriptInvocation(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var values = new[] { executable }.Concat(arguments).ToArray();
        if (values.Any(value => value.IndexOfAny(['\"', '\r', '\n', '%', '!']) >= 0))
        {
            throw new ArgumentException(
                "Windows command-script paths and arguments contain unsafe characters.");
        }

        return string.Join(" ", values.Select(value => $"\"{value}\""));
    }

    /// <summary>
    /// A line longer than this is cut rather than grown. The capture is bounded already;
    /// this bounds the one line a reader is handed, so a child that never writes a newline
    /// cannot make a caller hold its whole output in one string.
    /// </summary>
    private const int MaximumLineCharacters = 4_096;

    private async Task<CapturedText> DrainBoundedAsync(
        TextReader reader,
        CancellationToken cancellationToken,
        Action<string>? onLine = null)
    {
        var captured = new StringBuilder(
            Math.Min(_maximumCapturedCharacters, 4_096));
        var buffer = new char[4_096];
        var line = onLine is null ? null : new StringBuilder();
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
                if (line is not null)
                {
                    // Split here rather than after the run: what this reader is for is a
                    // caller who has to show the line while the run is still going.
                    for (var index = 0; index < read; index++)
                    {
                        var character = buffer[index];
                        if (character == '\n')
                        {
                            Emit(line, onLine!);
                        }
                        else if (character != '\r' && line.Length < MaximumLineCharacters)
                        {
                            line.Append(character);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        // A child that ended without a final newline still said something.
        if (line is { Length: > 0 })
        {
            Emit(line, onLine!);
        }

        return new CapturedText(captured.ToString(), truncated);
    }

    private static void Emit(StringBuilder line, Action<string> onLine)
    {
        var text = line.ToString();
        line.Clear();
        if (text.Length > 0)
        {
            onLine(text);
        }
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

    private static async Task CancelFileDrainsAsync(
        CancellationTokenSource drainCancellation,
        Task standardOutput,
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
