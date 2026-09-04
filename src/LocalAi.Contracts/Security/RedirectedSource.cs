namespace LocalAi.Contracts.Security;

/// <summary>
/// Where source-derived text may travel unmarked, decided once for every console in this product.
///
/// The MCP tools wrap unconditionally: everything they return crossed a protocol boundary into a
/// model's context, and a marker there costs nothing. A console has two readers, and the honest
/// split follows the content rather than the audience — a terminal has a person in front of it,
/// for whom markers are noise to scroll past, while anything redirected lands in a file, a pipe or
/// another program, where a model may meet it and must not act on instructions inside it.
///
/// The wrong guess is asymmetric. An unwanted wrapper costs a person one glance; a missing one
/// costs a safety boundary. So redirection wraps, and only a terminal does not.
///
/// This lives here, beside <see cref="UntrustedContent"/>, because both console binaries need it
/// and neither should reference the other. It was written for a local model's answer about files;
/// <c>codesearch get-chunk</c> then turned out to print a file's actual bytes with no marker in
/// any direction, which is the more direct hazard of the two.
/// </summary>
public static class RedirectedSource
{
    /// <param name="origin">
    /// What produced the text, in the shape the MCP tools use for the same attribute, so
    /// provenance reads the same in both faces.
    /// </param>
    /// <param name="redirected">
    /// <c>Console.IsOutputRedirected</c> at the call site, passed in rather than read here so this
    /// stays a decision anything can test.
    /// </param>
    public static string Wrap(string origin, string content, bool redirected) =>
        redirected ? UntrustedContent.Wrap(content, origin) : content;
}
