using System.Text.Json;
using CodeSearch.Cli;
using CodeSearch.Core.Search;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// Which of this binary's commands answer a program, and what they say.
/// </summary>
public sealed class CodeSearchConsoleJsonTests
{
    /// <summary>
    /// `index` and `overlay` stream their progress to standard output as they build, so an
    /// envelope at the end would leave a caller holding progress lines *and* an envelope — which
    /// breaks the one promise the flag makes. A plugin that wants an index built calls
    /// `localai sync`, which publishes a generation. `evaluate` already prints a JSON shape of its
    /// own, and `scan` has nobody asking.
    /// </summary>
    [Theory]
    [InlineData("search", true)]
    [InlineData("get-chunk", true)]
    [InlineData("status", true)]
    [InlineData("index", false)]
    [InlineData("overlay", false)]
    [InlineData("evaluate", false)]
    [InlineData("scan", false)]
    public void Only_the_commands_a_plugin_drives_fill_an_envelope(string command, bool supported)
    {
        Assert.Equal(supported, ConsoleJson.Supports(command));
    }

    /// <summary>
    /// "Can I search here yet" is one question, and it used to need two commands: this one for the
    /// index and `localai repo status` for whether the repository is connected at all. The verdict
    /// travels as the same token that command prints — one fact, one name, in both binaries.
    /// </summary>
    [Theory]
    [InlineData(true, "CONFIGURED")]
    [InlineData(false, "NOT_CONFIGURED")]
    public void The_status_answers_whether_the_repository_is_connected(
        bool connected,
        string expected)
    {
        var data = ConsoleJson.Describe(Sample(), connected);

        Assert.Equal(expected, data.Connected, StringComparer.Ordinal);
    }

    /// <summary>
    /// A generation published without a semantic index answers navigation from text matching and
    /// looks perfectly healthy in every other field — the vectors are current and the commit has
    /// not drifted. One token says which it is; the two ways of being heuristic differ
    /// diagnostically but not in remedy, and both are fixed by a re-sync.
    /// </summary>
    [Theory]
    [InlineData(true, false, "Precise")]
    [InlineData(false, false, "Heuristic")]
    [InlineData(true, true, "Heuristic")]
    public void The_status_says_whether_navigation_is_precise(
        bool present,
        bool coversNothing,
        string expected)
    {
        var data = ConsoleJson.Describe(
            Sample() with
            {
                SemanticIndexPresent = present,
                SemanticIndexCoversNothing = coversNothing,
            },
            connected: true);

        Assert.Equal(expected, data.Navigation, StringComparer.Ordinal);
    }

    /// <summary>
    /// The overlay is a thing with its own existence, so it nests rather than flattening into
    /// six fields with a prefix.
    /// </summary>
    [Fact]
    public void The_overlay_travels_as_its_own_object()
    {
        using var document = JsonDocument.Parse(
            MachineEnvelope.Answer("status", ConsoleJson.Describe(Sample(), connected: true)));

        var overlay = document.RootElement.GetProperty("data").GetProperty("overlay");

        Assert.False(overlay.GetProperty("built").GetBoolean());
        Assert.True(document.RootElement.GetProperty("data").GetProperty("built").GetBoolean());
    }

    /// <summary>
    /// The flag's promise has to survive failure: a caller that gets prose the moment something
    /// goes wrong has no machine mode. Each code names what the caller can act on — an index that
    /// was never built and one that is stale for this worktree need different answers.
    /// </summary>
    [Theory]
    [InlineData(typeof(SearchNotReadyException), "index_not_ready")]
    [InlineData(typeof(FileNotFoundException), "index_not_built")]
    [InlineData(typeof(InvalidOperationException), "unexpected_failure")]
    public void Every_failure_a_program_meets_carries_a_code(Type failure, string code)
    {
        var exception = (Exception)Activator.CreateInstance(failure, "went wrong")!;

        Assert.Equal(code, ConsoleJson.Classify(exception), StringComparer.Ordinal);
    }

    private static IndexStatus Sample() =>
        new(
            WorkingRoot: @"R:\repo",
            RepositoryRoot: @"R:\repo",
            IndexPath: @"C:\runtime\base.cidx",
            Exists: true,
            Model: "qwen3-embedding:8b-q8_0",
            Dim: 4096,
            FileCount: 654,
            ChunkCount: 7093,
            SizeBytes: 124_000_000,
            IndexedCommit: "c49997499",
            CurrentCommit: "c49997499",
            IndexedAtUtc: new DateTime(2026, 9, 3, 22, 1, 27, DateTimeKind.Utc),
            BaseRoot: @"R:\repo",
            RequiresOverlay: true,
            Overlay: new OverlayStatus(
                @"C:\runtime\overlay.cidx", false, 0, 0, 0, 0, "", "", default),
            SemanticIndexPresent: true);
}
