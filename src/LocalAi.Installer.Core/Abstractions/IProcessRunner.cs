namespace LocalAi.Installer.Core.Abstractions;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same run, with each line of standard error handed over as it arrives rather
    /// than at the end. A model download reports for minutes and finishes once, so a
    /// caller that only learns at the end learns nothing it could have shown.
    ///
    /// The captured text is unchanged: what a line-reader sees, the result still holds,
    /// bounded exactly as before.
    ///
    /// A runner that cannot stream inherits this, runs without streaming, and reports no
    /// lines — which is the truth about it rather than a silence dressed as none arriving.
    /// Every caller here already treats zero lines as ordinary: a fast run reports none.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string> onStandardErrorLine,
        CancellationToken cancellationToken) =>
        RunAsync(executable, arguments, timeout, cancellationToken);
}

/// <summary>
/// Runs a process while preserving its standard output as raw bytes in a file.
/// Use this for authenticated CLI downloads: routing an archive through a text reader
/// corrupts arbitrary byte sequences.
/// </summary>
public interface IProcessFileRunner
{
    Task<ProcessResult> RunToFileAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string outputPath,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed record ProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    bool StandardOutputTruncated = false,
    bool StandardErrorTruncated = false);

public enum ProcessTerminationCause
{
    Timeout,
    Cancellation,
}

public sealed class ProcessTerminationException : Exception
{
    public ProcessTerminationException(
        int processId,
        ProcessTerminationCause cause,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProcessId = processId;
        Cause = cause;
    }

    public int ProcessId { get; }
    public ProcessTerminationCause Cause { get; }
}
