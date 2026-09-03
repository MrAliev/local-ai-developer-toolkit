using System.Text;

namespace LocalAi.Contracts;

/// <summary>
/// Which encoding this process writes its own text in — the other half of
/// <see cref="ChildProcessText"/>, which answers the same question about somebody else's output.
///
/// On Windows a process that has not said otherwise writes in the console's output code page,
/// and on a Russian machine that is 866: no em dash, no ellipsis, no emoji, and the `🔧` that
/// opens every local-model notice becomes a question mark. Saying UTF-8 once, at the top of
/// Main, is the whole fix.
///
/// It lives here rather than in each entry point because it had been written out three times
/// and forgotten a fourth. <c>localai.exe</c> — the one a person types — never set it, while
/// <see cref="ChildProcessText.Utf8"/> and the code that starts <c>localai sync</c> both
/// described it as an executable that does. A decision copied four times is a decision that
/// drifts; a decision named once is one that can be tested.
/// </summary>
public static class ConsoleOutputText
{
    /// <summary>
    /// Says UTF-8 for this process's own output. True when the console accepted it, false when
    /// there was no console to tell.
    /// </summary>
    public static bool UseUtf8() => UseUtf8(encoding => Console.OutputEncoding = encoding);

    /// <summary>
    /// The decision without the console, so it can be checked. The caller supplies what to do
    /// with the encoding; everything worth testing — which encoding is asked for, and which
    /// failures are survivable — is on this side of that call.
    /// </summary>
    /// <param name="apply">Where the encoding goes. <see cref="Console.OutputEncoding"/> in a
    /// running process.</param>
    public static bool UseUtf8(Action<Encoding> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        try
        {
            apply(ChildProcessText.Utf8);
            return true;
        }
        catch (IOException)
        {
            // No console attached — a Git hook, or an MCP server started by a client. Output is
            // already UTF-8 there, so there is nothing to fix and nothing to report. Anything
            // else is a process in trouble, and travels.
            return false;
        }
    }
}
