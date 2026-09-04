using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The first two commands that reach a local model from a terminal.
///
/// Until now the whole local-model capability was behind MCP: an agent could summarise a file or
/// read a build log, and a person at a prompt could not, nor could an agent whose MCP server was
/// down — which is the fallback this product tells every machine to use.
/// </summary>
public sealed class LocalModelCommandTests
{
    /// <summary>
    /// The instruction is the one thing `ask` cannot default, so it is positional and required —
    /// and the refusal says the form, because a reader who got it wrong is looking right there.
    /// </summary>
    [Fact]
    public void Ask_without_an_instruction_says_what_the_form_is()
    {
        Assert.False(AskCommand.TryParse([], out _, out var refusal));

        Assert.Equal("prompt_missing", refusal!.Code, StringComparer.Ordinal);
        Assert.Contains("localai ask", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Files are positional and variadic because a shell glob is how anybody names sixty of them,
    /// and sixty-four is the limit the task itself enforces.
    /// </summary>
    [Fact]
    public void Ask_takes_its_files_after_the_instruction()
    {
        Assert.True(AskCommand.TryParse(
            ["list every TODO", @"src\Foo.cs", @"src\Bar.cs"],
            out var request,
            out _));

        Assert.Equal("list every TODO", request!.Prompt, StringComparer.Ordinal);
        Assert.Equal([@"src\Foo.cs", @"src\Bar.cs"], request.Files);
        Assert.Equal(LocalTaskProfile.ShortSummary, request.Profile);
    }

    /// <summary>
    /// One spelling of the option for every one of these commands. The MCP surface calls the same
    /// enum `mode` on one tool and `taskProfile` on three, and its own comment records what that
    /// cost: an agent was told to fix a parameter the tool it called does not have.
    /// </summary>
    [Fact]
    public void Ask_names_the_profiles_it_accepts_when_given_one_it_does_not()
    {
        Assert.False(AskCommand.TryParse(
            ["summarise", "--profile", "Ocr"],
            out _,
            out var refusal));

        Assert.Equal("profile_not_supported", refusal!.Code, StringComparer.Ordinal);
        Assert.Contains("ShortSummary", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ocr,", refusal.Message, StringComparison.Ordinal);

        // The cause has to be the one the code checks. "cannot hold a conversation with" was
        // false for four of the seven excluded profiles: translation and image profiles route
        // through the same chat call, and it is the task that does not fit, not the model.
        Assert.Contains("text-chat", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A word that is not a profile at all is a different mistake from a profile that
    /// exists but is for images.</summary>
    [Fact]
    public void Ask_tells_a_typo_apart_from_a_profile_meant_for_images()
    {
        Assert.False(AskCommand.TryParse(["summarise", "--profile", "Shortsummry"], out _, out var typo));

        Assert.Equal("profile_unknown", typo!.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// `dotnet build | localai triage` is the reason this command is worth having, so a log that
    /// was piped in needs no argument at all.
    /// </summary>
    [Fact]
    public void Triage_reads_what_was_piped_in_when_no_file_is_named()
    {
        Assert.True(TriageCommand.TryParse([], piped: true, out var request, out _));

        Assert.True(request!.FromStandardInput);
        Assert.Null(request.Path);
    }

    /// <summary>
    /// A dash is a filename to the option parser and a spelling of "standard input" to everybody
    /// else, so it has to be recognised before the test that refuses unknown options.
    /// </summary>
    [Fact]
    public void Triage_understands_a_dash_and_does_not_call_it_an_unknown_option()
    {
        Assert.True(TriageCommand.TryParse(["-"], piped: true, out var request, out var refusal));

        Assert.Null(refusal);
        Assert.True(request!.FromStandardInput);
    }

    /// <summary>
    /// Nothing named and nothing piped: reading standard input would hang a person's terminal
    /// with no indication of why, so it refuses instead.
    /// </summary>
    [Fact]
    public void Triage_refuses_rather_than_waiting_on_a_terminal_that_will_never_answer()
    {
        Assert.False(TriageCommand.TryParse([], piped: false, out _, out var refusal));

        Assert.Equal("source_missing", refusal!.Code, StringComparer.Ordinal);
        Assert.Contains("localai triage", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A named file means standard input is neither read nor claimed to be.</summary>
    [Fact]
    public void Triage_given_a_file_does_not_also_read_the_pipe()
    {
        Assert.True(TriageCommand.TryParse(
            [@"C:\build.log"],
            piped: true,
            out var request,
            out _));

        Assert.Equal(@"C:\build.log", request!.Path, StringComparer.Ordinal);
        Assert.False(request.FromStandardInput);
    }

    /// <summary>
    /// The refusal for a missing log points at the usage line, so the usage line has to carry the
    /// spelling that fixes it.
    /// </summary>
    [Fact]
    public void The_usage_lines_teach_the_forms_the_refusals_mention()
    {
        Assert.Contains("[--json]", CliUsage.Ask, StringComparison.Ordinal);
        Assert.Contains("[--json]", CliUsage.Triage, StringComparison.Ordinal);
        Assert.Contains("-", CliUsage.Triage, StringComparison.Ordinal);
        Assert.Contains(CliUsage.Ask, CliUsage.Text, StringComparison.Ordinal);
        Assert.Contains(CliUsage.Triage, CliUsage.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A command marked `[--json]` in the usage has to actually fill an envelope: the refusal for
    /// the flag sends readers to those marks, so a mark without an envelope is a dead end.
    /// </summary>
    [Theory]
    [InlineData("ask")]
    [InlineData("triage")]
    public void A_command_marked_in_the_usage_answers_the_flag(string command)
    {
        Assert.True(MachineOutput.Supports(command));
    }

    /// <summary>
    /// A residency of `PartialOffload` says the model did not fit; the percentage says how much
    /// of it arrived, which is what makes it information rather than a warning. The prose face
    /// has carried that figure all along and the wire carried only the verdict.
    ///
    /// Absent rather than null on a healthy run: a field that is present and empty on almost
    /// every call teaches a reader to skip it.
    /// </summary>
    [Fact]
    public void The_wire_says_how_much_of_the_model_arrived_when_some_of_it_did_not()
    {
        var degraded = MachineOutput.Answer(
            "ask",
            new LocalModelData(
                "answer", "ask:x", "qwen3.5:9b", "PartialOffload", 12, 340, 9, false, 62));
        var healthy = MachineOutput.Answer(
            "ask",
            new LocalModelData(
                "answer", "ask:x", "qwen3.5:9b", "None", 12, 340, 9, false, null));

        Assert.Contains("\"vramResidentPercent\":62", degraded, StringComparison.Ordinal);
        Assert.DoesNotContain("vramResidentPercent", healthy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The answer was written by a local model out of files it read, so wherever it can be read
    /// again later it has to carry that provenance. Piped or redirected, it is going somewhere a
    /// model may meet it, and it is wrapped; on a terminal the reader is a person and the markers
    /// are noise they have to scroll past.
    ///
    /// The wrong guess is asymmetric — an unwanted wrapper costs a person one glance, a missing
    /// one costs a safety boundary — so redirection wraps.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_answer_carries_its_provenance_wherever_it_can_be_read_again(
        bool redirected,
        bool wrapped)
    {
        var rendered = LocalModelOutput.Answer(
            "ask:" + @"R:\repo\src\Foo.cs",
            "src/Foo.cs:41  TODO: retry once the broker reports ready",
            redirected);

        Assert.Equal(
            wrapped,
            rendered.Contains("<untrusted-content", StringComparison.Ordinal));
        Assert.Contains("TODO: retry", rendered, StringComparison.Ordinal);
    }
}
