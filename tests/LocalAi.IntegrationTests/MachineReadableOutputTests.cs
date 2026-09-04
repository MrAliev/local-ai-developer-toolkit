using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Repository;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The face the console turns to a program rather than to a person.
///
/// The reader here is an editor plugin, and the one property it cannot do without is that the
/// bytes do not change with the machine they arrive from — which is exactly what this product
/// spent thirteen branches making untrue of its prose.
/// </summary>
public sealed class MachineReadableOutputTests
{
    /// <summary>
    /// Deciding and applying are separate, the way <c>OutputCulture.Resolve</c> and
    /// <c>OutputCulture.Apply</c> are: a pure answer anything may ask for, and the process-wide
    /// change only an entry point may make.
    /// </summary>
    [Fact]
    public void Asking_for_json_asks_for_one_language()
    {
        Assert.Equal("en", MachineOutput.Language(["repo", "status", "--json"]));
    }

    /// <summary>Nobody asked for JSON, so nothing was said about the language.</summary>
    [Fact]
    public void Without_the_flag_the_language_is_left_to_the_reader()
    {
        Assert.Null(MachineOutput.Language(["repo", "status"]));
    }

    /// <summary>
    /// The flag belongs to the envelope, not to the command. `repo status` refuses arguments it
    /// does not know, so passing it through would turn every JSON call into a refusal.
    /// </summary>
    [Fact]
    public void The_flag_does_not_reach_the_command_that_would_refuse_it()
    {
        Assert.Equal(
            ["repo", "status", "--root", @"C:\r"],
            MachineOutput.Without(["repo", "status", "--json", "--root", @"C:\r"]));
    }

    /// <summary>
    /// One envelope, versioned, for every command — so a plugin writes one parser, and a command
    /// added next year is readable by a plugin written today.
    /// </summary>
    [Fact]
    public void The_envelope_names_its_schema_and_the_command_that_filled_it()
    {
        using var document = JsonDocument.Parse(
            MachineOutput.Answer("repo status", new { status = "CONFIGURED" }));

        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("repo status", document.RootElement.GetProperty("command").GetString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
    }

    /// <summary>
    /// Half an envelope is never present: a plugin that finds `error` knows it need not look at
    /// `data`, and the absence is what makes that check cheap.
    /// </summary>
    [Fact]
    public void An_answer_carries_no_error_and_a_refusal_carries_no_data()
    {
        using var answer = JsonDocument.Parse(
            MachineOutput.Answer("repo status", new { status = "CONFIGURED" }));
        using var refusal = JsonDocument.Parse(
            MachineOutput.Refusal("repo status", "argument_unknown", "does not understand '-r'"));

        Assert.False(answer.RootElement.TryGetProperty("error", out _));
        Assert.False(refusal.RootElement.TryGetProperty("data", out _));
        Assert.False(refusal.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "argument_unknown",
            refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// The verdict travels as the token the prose already prints, not as a boolean. INITIALIZING
    /// is a real state in this product, and the day it reaches this command a boolean is a
    /// breaking change while a token is an added member.
    /// </summary>
    [Theory]
    [InlineData(true, "CONFIGURED")]
    [InlineData(false, "NOT_CONFIGURED")]
    public void The_status_travels_as_its_token(bool configured, string expected)
    {
        var identity = RepositoryIdentity.FromCommonDirectory(
            Path.Combine(Path.GetTempPath(), "machine-output", ".git"));

        var data = RepoCommand.MachineStatus(
            new RepositoryStatus(identity, configured, "prose nobody parses"));

        Assert.Equal(expected, data.Status, StringComparer.Ordinal);
        Assert.Equal(identity.Id, data.RepositoryId, StringComparer.Ordinal);
        Assert.Equal(identity.CommonDirectory, data.CommonDirectory, StringComparer.Ordinal);
    }

    /// <summary>
    /// The prose is what a person reads and what an agent is told to relay; it is reworded
    /// whenever it is wrong, which is exactly why it cannot be a wire contract.
    /// </summary>
    [Fact]
    public void The_prose_does_not_travel_with_the_data()
    {
        var identity = RepositoryIdentity.FromCommonDirectory(
            Path.Combine(Path.GetTempPath(), "machine-output", ".git"));
        var status = RepoCommand.Status(identity.CommonDirectory, Path.GetTempPath());

        var json = MachineOutput.Answer("repo status", RepoCommand.MachineStatus(status));

        Assert.DoesNotContain("offer", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flag's promise has to be unconditional: if `--json` was passed, stdout is an envelope.
    /// A command that ignored it would print prose that changes language with the machine — the
    /// precise defect this mode exists to remove.
    /// </summary>
    [Fact]
    public void A_command_with_no_envelope_refuses_the_flag_rather_than_ignoring_it()
    {
        var message = CliText.JsonNotSupported;

        Assert.Contains("--json", message, StringComparison.Ordinal);

        // Deliberately no echo of what was typed. Naming the command meant naming a prefix of
        // it — `hooks` for `hooks install` — which is a command the reader did not run.
        Assert.Contains("[--json]", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A command path cannot be guessed from the leading words: `ask` takes the reader's own
    /// instruction as its first positional argument, so "the first two non-option tokens" made
    /// `localai ask "In one sentence: what is this?" --json` ask for a command called
    /// `ask In one sentence: what is this?` and be told the flag was unavailable.
    /// </summary>
    [Theory]
    [InlineData(new[] { "repo", "status", "--json" }, "repo status")]
    [InlineData(new[] { "ask", "summarise this file", "a.cs", "--json" }, "ask")]
    [InlineData(new[] { "triage", "build.log", "--json" }, "triage")]
    public void The_command_is_matched_against_what_exists_not_guessed(
        string[] arguments,
        string expected)
    {
        Assert.Equal(expected, MachineOutput.Enveloped(arguments), StringComparer.Ordinal);
    }

    /// <summary>
    /// A command with no envelope matches nothing, and what it is called comes from its first
    /// word only — the words after it may be anything the reader typed.
    /// </summary>
    [Fact]
    public void A_command_with_no_envelope_matches_nothing_and_is_named_by_its_first_word()
    {
        Assert.Null(MachineOutput.Enveloped(["prune", "--dry-run", "--json"]));
        Assert.Equal("prune", MachineOutput.Named(["prune", "--dry-run", "--json"]));
        Assert.Equal(string.Empty, MachineOutput.Named(["--json"]));
    }

    /// <summary>
    /// Every refusal a program can meet carries a token it can branch on. The tokens are
    /// `subject_state` and never name the command — `command` already says that, and `--root`
    /// without a value happens to four commands, not one.
    /// </summary>
    [Theory]
    [InlineData(new[] { "--root" }, "root_value_missing")]
    [InlineData(new[] { "-r", @"C:\repo" }, "argument_unknown")]
    [InlineData(new[] { "one", "two" }, "repository_ambiguous")]
    public void Each_parse_refusal_carries_a_code(string[] arguments, string code)
    {
        Assert.False(RepoCommand.TryParseStatusArguments(arguments, out _, out var refusal));

        Assert.Equal(code, refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// The whole of the split: what a program switches on cannot move with the machine, and what
    /// a person reads has to. One refusal carries both, and they are not the same field.
    /// </summary>
    [Fact]
    public void The_code_stays_put_while_the_message_follows_the_reader()
    {
        RepoCommand.TryParseStatusArguments(["--root"], out _, out var english);

        using var reading = TestCulture.Reading("ru");
        RepoCommand.TryParseStatusArguments(["--root"], out _, out var russian);

        Assert.Equal(english!.Code, russian!.Code, StringComparer.Ordinal);
        Assert.NotEqual(english.Message, russian.Message, StringComparer.Ordinal);
    }

    /// <summary>
    /// `localai model` has printed a JSON envelope of its own since before this mode existed,
    /// with `schemaVersion` where this one has `schema`. Two envelopes in one binary is a fact a
    /// plugin author has to be told, and the field name is what tells them apart.
    /// </summary>
    [Fact]
    public void The_new_envelope_does_not_collide_with_the_one_model_already_prints()
    {
        using var document = JsonDocument.Parse(
            MachineOutput.Answer("repo status", new { status = "CONFIGURED" }));

        Assert.False(document.RootElement.TryGetProperty("schemaVersion", out _));
    }
}
