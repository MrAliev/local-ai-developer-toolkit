using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The one command a plugin calls before any other: which of this binary's commands answer a
/// program, and in what shape.
///
/// Without it the alternative is a plugin that hard-codes the answer — that `localai ask` takes
/// `--json` and `localai sync` does not, that `model status` spells its version `schemaVersion`
/// while everything else spells it `schema`. A copy in somebody else's repository is a copy that
/// cannot be corrected here, which is why the listing has to be derived from the very arrays that
/// decide the behaviour rather than written out beside them. A listing that can disagree with what
/// the binary does is worse than no listing at all, so most of what follows is about that
/// derivation holding.
/// </summary>
public sealed class CapabilityListingTests
{
    /// <summary>
    /// Both binaries answer with <c>command: "capabilities"</c>, so without this field the two
    /// listings are indistinguishable — and discovery is two calls, because neither binary knows
    /// the other's surface.
    /// </summary>
    [Fact]
    public void The_listing_says_which_binary_answered()
    {
        Assert.Equal("localai", MachineOutput.Capabilities().Binary, StringComparer.Ordinal);
    }

    /// <summary>
    /// Trivially true while the listing is derived, and the whole point of the test: it is what
    /// fails the build the day somebody flattens the derivation into a literal that then drifts.
    /// </summary>
    [Fact]
    public void The_enveloped_commands_are_exactly_the_ones_that_fill_an_envelope()
    {
        Assert.Equal(
            MachineOutput.Commands,
            MachineOutput.Capabilities().Commands
                .Where(command => command.Shape == MachineEnvelope.VersionField)
                .Select(command => command.Name)
                .ToArray());
    }

    /// <summary>
    /// The listing is itself a command a program drives, so it appears in its own answer — and
    /// because it is in the same array, the flag reaches it through the check every other
    /// enveloped command goes through, with no special case anywhere.
    /// </summary>
    [Fact]
    public void The_listing_names_itself()
    {
        Assert.Contains("capabilities", MachineOutput.Commands, StringComparer.Ordinal);
    }

    /// <summary>
    /// The mapping to MCP is the one field here that exists nowhere else in the code, so it is a
    /// first copy rather than a second — and a first copy still has to be true. The names are held
    /// against <see cref="McpToolNames"/>, which <c>LocalLm.Tests</c> in turn holds against the
    /// attributes the server actually exposes: a renamed tool breaks the build rather than the
    /// listing.
    /// </summary>
    [Fact]
    public void Every_tool_named_is_one_the_local_model_server_exposes()
    {
        var named = MachineOutput.Capabilities().Commands
            .Select(command => command.Tool)
            .OfType<string>()
            .ToArray();

        Assert.NotEmpty(named);
        Assert.All(named, tool => Assert.Contains(tool, McpToolNames.LocalLm, StringComparer.Ordinal));
    }

    /// <summary>
    /// The listing is the inventory for a program; the usage block is the inventory for a person.
    /// They describe the same binary, and the block is where the syntax lives, so a command in one
    /// and not the other means one of them is lying. The `hook` command was missing from the usage
    /// block while every installed Git hook invoked it, which is what that costs.
    /// </summary>
    [Fact]
    public void Every_listed_command_appears_in_the_usage_block()
    {
        Assert.All(
            MachineOutput.Capabilities().Commands,
            command => Assert.Contains(
                "localai " + command.Name,
                CliUsage.Text,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// `model` printed a JSON shape of its own before the envelope existed, and its version field
    /// is spelled differently. That difference is the whole reason `shape` carries the *name* of
    /// the field rather than a category: it is the property a parser reads to know which of the
    /// shapes it is holding.
    ///
    /// Asserted against the record the command actually serialises, so the listing cannot claim a
    /// spelling the output does not have.
    /// </summary>
    [Fact]
    public void A_command_that_predates_the_envelope_names_the_field_that_carries_its_version()
    {
        var shape = MachineOutput.Capabilities().Commands
            .Single(command => command.Name == "model status")
            .Shape;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            new ModelStatusCommandSuccess(1, "status", true, "1", [], [])));

        Assert.True(document.RootElement.TryGetProperty(shape, out _));
        Assert.False(document.RootElement.TryGetProperty(MachineEnvelope.VersionField, out _));
    }

    /// <summary>
    /// A listing nobody parses is a listing that goes stale, so the flag is required rather than
    /// optional here — and the refusal says which inventory a person wanted instead.
    /// </summary>
    [Fact]
    public void The_listing_is_for_a_program_and_says_so_when_the_flag_is_missing()
    {
        var refusal = CapabilitiesCommand.Refused(["capabilities"], machineReadable: false);

        Assert.Equal("json_required", refusal?.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// It takes no arguments, and an argument silently ignored is a caller who believes something
    /// happened. `repo status` refuses what it does not understand; so does this.
    /// </summary>
    [Fact]
    public void An_argument_it_does_not_know_is_refused_rather_than_ignored()
    {
        var refusal = CapabilitiesCommand.Refused(
            ["capabilities", "--verbose"],
            machineReadable: true);

        Assert.Equal("argument_unknown", refusal?.Code, StringComparer.Ordinal);
        Assert.Contains("--verbose", refusal!.Message, StringComparison.Ordinal);
    }

    /// <summary>And with the flag and nothing else, there is nothing to refuse.</summary>
    [Fact]
    public void With_the_flag_and_no_arguments_it_answers()
    {
        Assert.Null(CapabilitiesCommand.Refused(["capabilities"], machineReadable: true));
    }
}
