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
/// Deliberately not a stack trace by default. The guard in Program.cs exists because a raw
/// stack told an operator nothing; this keeps that decision and adds the identification the
/// next bug report needs. But locating #188 took the reporter a local rebuild whose only
/// change was appending the stack — so the stack is available on request:
/// LOCALAI_STACK=1 appends the full exception under the line, and costs nothing shipped.
/// </summary>
internal static class UnexpectedFailure
{
    /// <summary>Set to <c>1</c> (or <c>true</c>) to append the full exception.</summary>
    internal const string StackVariableName = "LOCALAI_STACK";

    public static string Describe(Exception exception) =>
        Describe(exception, Environment.GetEnvironmentVariable(StackVariableName));

    internal static string Describe(Exception exception, string? stackSwitch)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        var line = string.Join(" -> ", parts);
        return WantsStack(stackSwitch)
            ? line + Environment.NewLine + exception
            : line;
    }

    private static bool WantsStack(string? value) =>
        value?.Trim() is { } switched &&
        (switched == "1" ||
         switched.Equals("true", StringComparison.OrdinalIgnoreCase));
}
