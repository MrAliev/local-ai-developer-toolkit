using LocalAi.Contracts.Security;

namespace LocalAi.Cli;

/// <summary>
/// How a local model's answer leaves this process.
///
/// Two facts decide it. The answer is the product — <c>localai ask "summarise" x.cs &gt; out.md</c>
/// has to leave a file holding the answer and nothing else — so the answer goes to standard output
/// and the notice about the run goes to standard error, the way <c>hook</c> already puts its
/// synchronization line there. And the answer is content a local model derived from files, so
/// wherever it can be read again later it carries the provenance markers the MCP face always adds.
/// </summary>
internal static class LocalModelOutput
{
    /// <summary>
    /// The answer, wrapped when it is going somewhere it can be re-read.
    ///
    /// A terminal has a person in front of it, and markers there are noise to scroll past. Piped
    /// or redirected, the text lands in a file, a log or another program, where a model may meet
    /// it and must not act on instructions inside it.
    ///
    /// The guess is asymmetric: an unwanted wrapper costs a person one glance, and a missing one
    /// costs a safety boundary — so redirection wraps. The case this cannot cover is an agent on
    /// a pseudo-terminal, which reads as interactive; it still gets the notice naming the command
    /// and the model, so provenance is dimmed rather than lost.
    /// </summary>
    public static string Answer(string origin, string answer, bool redirected) =>
        redirected ? UntrustedContent.Wrap(answer, origin) : answer;
}
