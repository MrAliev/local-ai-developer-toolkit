using System.Text.Json;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// The envelope, and the two ways of finding the flag that asks for it.
///
/// `localai` may scan its whole argument list, because no option value there could plausibly be
/// the literal `--json`. `codesearch search --query "--json"` is an ordinary query, so this binary
/// scans past an option's value.
/// </summary>
public sealed class MachineEnvelopeTests
{
    [Fact]
    public void A_flag_of_its_own_is_a_request_for_the_envelope()
    {
        Assert.True(MachineEnvelope.RequestedAsOption(["search", "--query", "auth", "--json"]));
    }

    /// <summary>
    /// The hazard this guards against is not reachable through today's parser, which discards a
    /// value beginning with `--`. It is guarded anyway because the parser is the thing more likely
    /// to be fixed than this scan is to be revisited.
    /// </summary>
    [Fact]
    public void A_query_that_happens_to_be_the_flag_is_not_a_request()
    {
        Assert.False(MachineEnvelope.RequestedAsOption(["search", "--query", "--json"]));
    }

    [Fact]
    public void The_flag_is_taken_out_of_what_the_command_sees()
    {
        Assert.Equal(
            ["search", "--query", "auth"],
            MachineEnvelope.WithoutFlag(["search", "--json", "--query", "auth"]));
    }

    /// <summary>
    /// One envelope for both binaries: a plugin writes one parser, and `schema` is the field that
    /// separates it from the two legacy shapes that spell their version `schemaVersion`.
    /// </summary>
    [Fact]
    public void The_envelope_is_the_same_shape_both_binaries_produce()
    {
        using var document = JsonDocument.Parse(
            MachineEnvelope.Answer("status", new { connected = "CONFIGURED" }));

        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("status", document.RootElement.GetProperty("command").GetString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("schemaVersion", out _));
    }

    [Fact]
    public void A_refusal_carries_a_code_and_no_data()
    {
        using var document = JsonDocument.Parse(
            MachineEnvelope.Refusal("search", "query_missing", "search needs --query"));

        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("data", out _));
        Assert.Equal(
            "query_missing",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
