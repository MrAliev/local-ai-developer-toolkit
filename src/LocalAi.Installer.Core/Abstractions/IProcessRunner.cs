namespace LocalAi.Installer.Core.Abstractions;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
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
