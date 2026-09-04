using System.Xml.Linq;
using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The last four CLI files that still spoke English only. Each carried a sentence that was wrong
/// on its own terms before any question of language came up, so those are asserted first.
/// </summary>
public sealed class LastFourCommandsFollowTheReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-lastfour-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The refusal is about to say that nothing was installed, and that has to be true. The write
    /// loop runs over four events in order, so a collision on the third used to leave the first
    /// two dispatchers written and the reader's own hooks already moved aside.
    /// </summary>
    [Fact]
    public void A_blocked_chain_installs_no_hook_at_all()
    {
        var hooks = Path.Combine(_root, "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(
            Path.Combine(hooks, "post-merge"),
            "#!/bin/sh\necho written by something that is not LocalAi\n");
        File.WriteAllText(
            Path.Combine(hooks, "post-merge.pre-localai"),
            "#!/bin/sh\necho saved on an earlier install\n");

        Assert.Throws<InvalidOperationException>(() => HookInstaller.Install(
            _root,
            Path.Combine(_root, "launcher", "localai-launcher.exe"),
            ["run", "localai"]));

        Assert.False(
            File.Exists(Path.Combine(hooks, "post-commit")),
            "post-commit comes before post-merge, so a refusal after it is a half-install");
    }

    /// <summary>
    /// Which two files collided, and what the reader can do about it. The old message named the
    /// backup only, and left the decision — which of the two hooks should run — unstated.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_blocked_chain_names_both_files_and_the_way_out(string language)
    {
        using var reading = TestCulture.Reading(language);

        var message = CliText.HooksChainBlocked(
            @"C:\r\.git\hooks\post-merge",
            @"C:\r\.git\hooks\post-merge.pre-localai");

        Assert.Contains(
            @"C:\r\.git\hooks\post-merge.pre-localai",
            message,
            StringComparison.Ordinal);
        Assert.Contains("localai hooks install", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A person typing an operation name is shown the ones that exist, and is not shown the
    /// parameter name of the method that rejected them.
    /// </summary>
    [Fact]
    public async Task An_unknown_native_operation_lists_the_operations_that_exist()
    {
        var refusal = await Assert.ThrowsAsync<ArgumentException>(
            () => NativeCommand.ExecuteAsync(
                "chatt",
                requestPath: null,
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain("Parameter", refusal.Message, StringComparison.Ordinal);
        foreach (var operation in Enum.GetNames<NativeOllamaOperation>())
        {
            Assert.Contains(operation, refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One typo, one answer. `semantic bogus` printed usage and exited 2; the same typo with a
    /// position attached reached the switch inside the try and exited 1 with a different sentence.
    /// </summary>
    [Fact]
    public void The_same_semantic_typo_gets_the_same_answer_either_way()
    {
        var bare = SemanticNavigationCommand.Execute(["bogus"], _root);
        var positioned = SemanticNavigationCommand.Execute(
            ["bogus", "--path", "a.cs", "--line", "1", "--column", "1"],
            _root);

        Assert.Equal(bare, positioned);
    }

    /// <summary>
    /// `--request` is read by `native`. `semantic` reads `--path`, `--line`, `--column` and the
    /// rest; usage offered a flag it ignores.
    /// </summary>
    [Fact]
    public void Usage_does_not_offer_semantic_a_flag_it_never_reads()
    {
        Assert.DoesNotContain(
            "semantic <operation> [--request",
            CliUsage.Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// `repo status -r C:\x` said only that `-r` was not understood. The flag that does work is
    /// one character away, and the refusal is where the reader is looking.
    /// </summary>
    [Fact]
    public void The_unknown_argument_refusal_names_the_flag_that_works()
    {
        using var reading = TestCulture.Reading("en");

        Assert.False(RepoCommand.TryParseStatusArguments(
            ["-r", @"C:\repo"],
            out _,
            out var error));

        Assert.Contains("--root", error!, StringComparison.Ordinal);
    }

    /// <summary>The three parse refusals are sentences, and sentences follow the reader.</summary>
    [Fact]
    public void The_repo_status_refusals_are_not_the_same_text_in_both_languages()
    {
        RepoCommand.TryParseStatusArguments(["--root"], out _, out var english);

        using var reading = TestCulture.Reading("ru");
        RepoCommand.TryParseStatusArguments(["--root"], out _, out var russian);

        Assert.NotEqual(english, russian, StringComparer.Ordinal);
    }

    /// <summary>
    /// The verdict is a status token, then a path, then prose. Documents and tests match on the
    /// token and agents read the prose; only one of the two translates.
    /// </summary>
    [Fact]
    public void The_not_configured_verdict_keeps_its_token_and_translates_its_offer()
    {
        using var reading = TestCulture.Reading("ru");

        var status = RepoCommand.Status(Path.Combine(_root, ".git"), _root);

        Assert.StartsWith("NOT_CONFIGURED: ", status.Message, StringComparison.Ordinal);
        Assert.Contains("CodeSearch", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("offer", status.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CLI is what the instruction block offers when the MCP server is unreachable, and that
    /// block quotes this refusal as a literal. The two assemblies cannot share one resource, so
    /// the duplicate is deliberate — and a duplicate with nothing watching it drifts.
    /// </summary>
    [Theory]
    [InlineData("CliText.resx", "CodeSearchText.resx")]
    [InlineData("CliText.ru.resx", "CodeSearchText.ru.resx")]
    public void The_position_refusal_is_word_for_word_the_one_the_mcp_tool_prints(
        string cli,
        string mcp)
    {
        var root = RepositoryRoot();

        Assert.Equal(
            Value(
                Path.Combine(root, "src", "CodeSearch.Mcp", "Resources", mcp),
                "PositionNotFromOne"),
            Value(
                Path.Combine(root, "src", "LocalAi.Cli", "Resources", cli),
                "PositionNotFromOne"),
            StringComparer.Ordinal);
    }

    private static string Value(string resx, string key) =>
        XDocument.Load(resx)
            .Root!
            .Elements("data")
            .Single(entry => (string?)entry.Attribute("name") == key)
            .Element("value")!
            .Value;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("No LocalAi.slnx above " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
