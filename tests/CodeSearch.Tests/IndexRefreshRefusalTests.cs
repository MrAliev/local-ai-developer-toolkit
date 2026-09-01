using CodeSearch.Mcp;

namespace CodeSearch.Tests;

/// <summary>
/// `index_refresh` is pre-approved because its description promises it refuses large work
/// inline and hands back a command to run in the background (#275). The sync it shells out to
/// now enforces that; this is the half the caller reads.
/// </summary>
public sealed class IndexRefreshRefusalTests
{
    private const string Root = @"R:\Repo";
    private const string Command = @"""C:\launcher\localai-launcher.exe"" run localai sync";

    [Fact]
    public void A_refusal_is_recognised_and_carries_the_command_to_run_instead()
    {
        var message = CodeSearchTools.RefusalMessage(
            Root,
            "REFUSED repository=abc files=612 limit=200 overlays=0",
            Command,
            limit: 200);

        Assert.NotNull(message);
        Assert.Contains("612", message);
        Assert.Contains("200", message);
        Assert.Contains(Command, message);
        Assert.Contains(Root, message);
    }

    /// <summary>
    /// A run that did the work reads as it always did. Getting this wrong turns every
    /// successful refresh into a refusal notice.
    /// </summary>
    [Fact]
    public void A_completed_run_is_not_a_refusal()
    {
        Assert.Null(CodeSearchTools.RefusalMessage(
            Root,
            "SYNCED repository=abc generation=def overlays=1",
            Command,
            limit: 200));
    }

    /// <summary>
    /// The number in the message is the work that was declined, not the limit repeated: a
    /// reader decides whether to run it in the background by how big it actually is.
    /// </summary>
    [Fact]
    public void The_message_reports_the_declined_work_rather_than_the_limit()
    {
        var message = CodeSearchTools.RefusalMessage(
            Root,
            "REFUSED repository=abc files=350 limit=200 overlays=0",
            Command,
            limit: 200);

        Assert.Contains("350 files", message);
    }
}
