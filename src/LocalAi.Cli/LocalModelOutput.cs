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
    /// The rule itself is <see cref="RedirectedSource"/>, in Contracts, because the other console
    /// binary needs the same one and neither should reference the other. The case it cannot cover
    /// is an agent on a pseudo-terminal, which reads as interactive: it still gets the notice
    /// naming the command and the model, so provenance is dimmed rather than lost.
    /// </summary>
    public static string Answer(string origin, string answer, bool redirected) =>
        RedirectedSource.Wrap(origin, answer, redirected);
}
