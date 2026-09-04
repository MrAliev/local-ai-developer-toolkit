using CodeSearch.Cli;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// The same discovery command `localai` answers, for the other binary — and it has to be the other
/// binary, because neither console references the other and neither can honestly describe a
/// surface it cannot see. Two calls, and the documentation says so.
///
/// What the tests are about is the derivation: the listing is the arrays that decide behaviour,
/// iterated. A second inventory beside the first is the thing this must never become.
/// </summary>
public sealed class CapabilityListingTests
{
    /// <summary>
    /// Both binaries answer with <c>command: "capabilities"</c>. Without this field a plugin
    /// holding two envelopes cannot tell which console sent which.
    /// </summary>
    [Fact]
    public void The_listing_says_which_binary_answered()
    {
        Assert.Equal("codesearch", ConsoleJson.Capabilities().Binary, StringComparer.Ordinal);
    }

    /// <summary>
    /// Trivially true while the listing is derived, and the point of the test: it fails the day
    /// somebody flattens the derivation into a literal that then drifts from the array the
    /// <c>--json</c> check actually reads.
    /// </summary>
    [Fact]
    public void The_enveloped_commands_are_exactly_the_ones_that_fill_an_envelope()
    {
        Assert.Equal(
            ConsoleJson.Commands,
            ConsoleJson.Capabilities().Commands
                .Where(command => command.Shape == MachineEnvelope.VersionField)
                .Select(command => command.Name)
                .ToArray());
    }

    /// <summary>
    /// Held against <see cref="McpToolNames"/>, which <c>McpToolInventoryTests</c> in turn holds
    /// against the attributes this server exposes. A renamed tool breaks the build rather than the
    /// listing.
    /// </summary>
    [Fact]
    public void Every_tool_named_is_one_this_server_exposes()
    {
        var named = ConsoleJson.Capabilities().Commands
            .Select(command => command.Tool)
            .OfType<string>()
            .ToArray();

        Assert.NotEmpty(named);
        Assert.All(
            named,
            tool => Assert.Contains(tool, McpToolNames.CodeSearch, StringComparer.Ordinal));
    }

    /// <summary>
    /// The listing is the inventory for a program and the usage block is the inventory for a
    /// person. A command in one and not the other means one of them is lying about this binary.
    /// </summary>
    [Fact]
    public void Every_listed_command_appears_in_the_usage_block()
    {
        Assert.All(
            ConsoleJson.Capabilities().Commands,
            command => Assert.Contains(
                "codesearch " + command.Name,
                CodeSearchUsage.Text,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// `evaluate` printed a JSON shape of its own before the envelope existed, and spells its
    /// version field differently. Carrying the *name* of that field rather than a category is what
    /// lets a plugin read the right property without knowing which shape it holds.
    /// </summary>
    [Fact]
    public void The_benchmark_shape_that_predates_the_envelope_is_listed_as_itself()
    {
        var evaluate = ConsoleJson.Capabilities().Commands
            .Single(command => command.Name == "evaluate");

        Assert.Equal("schemaVersion", evaluate.Shape, StringComparer.Ordinal);
        Assert.NotEqual(MachineEnvelope.VersionField, evaluate.Shape, StringComparer.Ordinal);
    }
}
