namespace LocalAi.Cli;

/// <summary>
/// Renders an unexpected exception as a line that identifies itself.
///
/// Issue #139 is the failure this exists for: a released CLI printed exactly
/// "Dll was not found." — the default message of a bare DllNotFoundException, carrying
/// neither the exception type, nor the library name, nor what threw it. Five reproduction
/// attempts later the cause is still unknown, because the one line the machine produced
/// named nothing. Message text alone is only as good as whoever wrote the message; the
/// type names and the inner chain are the part the runtime guarantees.
///
/// Deliberately not a stack trace. The guard in Program.cs exists because a raw stack
/// told an operator nothing; this keeps that decision and adds the identification the
/// next bug report needs.
/// </summary>
internal static class UnexpectedFailure
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", parts);
    }
}
