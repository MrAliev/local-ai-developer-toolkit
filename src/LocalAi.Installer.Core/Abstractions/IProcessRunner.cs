namespace LocalAi.Installer.Core.Abstractions;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
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
