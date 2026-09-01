using LocalAi.Contracts;

namespace LocalAi.Repository.Tests;

/// <summary>
/// The writer and the reader of a refusal live in different processes — `localai sync` prints
/// the line, the `index_refresh` MCP tool parses it out of stdout. Spelled out on both sides,
/// a rename on one would leave the other reading every refusal as an ordinary result, which is
/// #275 coming back through a different door. These pin the pair to each other.
/// </summary>
public sealed class SyncRefusalTests
{
    [Fact]
    public void What_the_writer_prints_is_what_the_reader_finds()
    {
        var line = SyncRefusal.Line("abc123", files: 612, limit: 200);

        Assert.Equal(612, SyncRefusal.Files(line));
    }

    /// <summary>Console.WriteLine ends lines with CRLF on Windows; the reader has to cope.</summary>
    [Fact]
    public void Windows_line_endings_do_not_hide_a_refusal()
    {
        var output = "Scanned something.\r\n" + SyncRefusal.Line("abc123", 612, 200) + "\r\n";

        Assert.Equal(612, SyncRefusal.Files(output));
    }

    /// <summary>
    /// A run that did the work is not a refusal. Getting this wrong turns every successful
    /// refresh into a refusal notice.
    /// </summary>
    [Theory]
    [InlineData("SYNCED repository=abc generation=def overlays=1")]
    [InlineData("")]
    [InlineData("REFUSED repository=abc overlays=0")]
    public void Anything_that_is_not_a_refusal_reads_as_none(string output)
    {
        Assert.Null(SyncRefusal.Files(output));
    }
}
