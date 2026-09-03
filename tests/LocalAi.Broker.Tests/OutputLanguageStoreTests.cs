using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The manual override, kept where every process that prints can read it.
///
/// The installer remembers its own answer beside its logs, because it asks the question on
/// screen. Nothing else asks: the CLI and the two MCP servers are started by a launcher and
/// print their first line before anybody could answer anything. So the choice is made once with
/// a command and read from the settings directory, the same way the residency and update-check
/// policies are.
///
/// Absent means "follow the machine", not "English". A missing file is the ordinary state of a
/// working installation, not a failure to report.
/// </summary>
public sealed class OutputLanguageStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-language-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void A_machine_that_was_never_asked_follows_the_machine()
    {
        Assert.Null(new OutputLanguageStore(root).Read());
    }

    [Fact]
    public void A_chosen_language_outlives_the_process_that_chose_it()
    {
        new OutputLanguageStore(root).Write("ru");

        Assert.Equal("ru", new OutputLanguageStore(root).Read());
    }

    /// <summary>
    /// Clearing it has to be reachable, or the first person to try the switch is stuck with
    /// their experiment for good.
    /// </summary>
    [Fact]
    public void Clearing_the_choice_returns_to_following_the_machine()
    {
        var store = new OutputLanguageStore(root);
        store.Write("ru");

        store.Write(null);

        Assert.Null(new OutputLanguageStore(root).Read());
    }

    /// <summary>
    /// A preferences file may never stop a tool from answering. There is a perfectly good answer
    /// available without it — the one the operating system gives.
    /// </summary>
    [Fact]
    public void An_unreadable_file_reads_as_no_choice_rather_than_as_a_failure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(
            OutputLanguageStore.PathFor(root))!);
        File.WriteAllText(OutputLanguageStore.PathFor(root), "{ this is not json");

        Assert.Null(new OutputLanguageStore(root).Read());
    }
}
