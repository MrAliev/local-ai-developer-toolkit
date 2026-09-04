using LocalAi.Contracts.Security;

namespace CodeSearch.Tests;

/// <summary>
/// One rule about where source-derived text may travel unmarked, in one place.
///
/// It was decided for the local-model commands — a person at a terminal gets bare text, anything
/// redirected carries its provenance — and then `codesearch get-chunk` turned out to print a
/// file's actual bytes with no marker in any direction, which is more directly injection-bearing
/// than anything a model summarises. The rule now lives beside <c>UntrustedContent</c>, which both
/// binaries already reference, rather than in one of them.
/// </summary>
public sealed class RedirectedSourceTests
{
    /// <summary>
    /// Piped or redirected, the text lands where a model may read it later, and it must not be
    /// possible to mistake instructions inside it for instructions to follow.
    /// </summary>
    [Fact]
    public void Redirected_source_carries_its_provenance()
    {
        var rendered = RedirectedSource.Wrap(
            "get-chunk:src/Foo.cs",
            "public void Run() { }",
            redirected: true);

        Assert.Contains("<untrusted-content", rendered, StringComparison.Ordinal);
        Assert.Contains("get-chunk:src/Foo.cs", rendered, StringComparison.Ordinal);
        Assert.Contains("public void Run() { }", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A terminal has a person in front of it, and markers there are noise they scroll past. The
    /// wrong guess is asymmetric — an unwanted wrapper costs one glance, a missing one costs the
    /// boundary — so redirection wraps and only a terminal does not.
    /// </summary>
    [Fact]
    public void A_person_at_a_terminal_gets_the_text_itself()
    {
        var rendered = RedirectedSource.Wrap(
            "get-chunk:src/Foo.cs",
            "public void Run() { }",
            redirected: false);

        Assert.Equal("public void Run() { }", rendered, StringComparer.Ordinal);
    }
}
