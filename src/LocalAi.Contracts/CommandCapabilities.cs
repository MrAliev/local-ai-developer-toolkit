using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// What one command tells a program about itself.
///
/// <c>shape</c> is the literal name of the version field that command's output carries — not a
/// category. It is the property a parser reads first to know which shape it is holding, which is
/// the discriminator <see cref="MachineEnvelope"/> already relies on, so naming it is worth more
/// than a judgement like "legacy" that would age badly.
///
/// <c>tool</c> is the MCP tool that does the same job, absent when there is none. That absence is
/// information a caller cannot get any other way: <c>local_models_sync</c> queues the recommended
/// missing set rather than a named model, so <c>model pull</c> genuinely has no equivalent.
/// </summary>
public sealed record CommandCapability(
    [property: JsonRequired, JsonPropertyName("name"), JsonPropertyOrder(0)]
    string Name,
    [property: JsonRequired, JsonPropertyName("shape"), JsonPropertyOrder(1)]
    string Shape,
    [property: JsonPropertyName("tool"), JsonPropertyOrder(2),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Tool);

/// <summary>
/// What one console can be driven to do.
///
/// Only commands a program can drive are listed: a command with no machine shape is one a plugin
/// cannot use, because parsing prose is the thing this exists to remove. Absence therefore means
/// "cannot drive this" and never "not installed" — everything else stays in the usage block, which
/// is the person's inventory and carries the syntax this deliberately does not.
///
/// <c>binary</c> is here because both consoles answer with <c>command: "capabilities"</c>, so
/// without it two listings are indistinguishable. There is no version field, on purpose: the
/// command list is the feature detection that cannot go stale, and a version for a bug report
/// comes from <c>localai doctor</c>, which knows the difference between the installed release and
/// the binary that happens to be running.
/// </summary>
public sealed record CapabilityData(
    [property: JsonRequired, JsonPropertyName("binary"), JsonPropertyOrder(0)]
    string Binary,
    [property: JsonRequired, JsonPropertyName("commands"), JsonPropertyOrder(1)]
    IReadOnlyList<CommandCapability> Commands);

/// <summary>
/// One listing, assembled the same way in both consoles — which is the point: a plugin joins the
/// two answers on <c>name</c>, and it can only do that if the two were built by the same rule.
/// </summary>
public static class CommandCapabilities
{
    /// <summary>
    /// The lists that decide behaviour, iterated. <paramref name="enveloped"/> is the array the
    /// <c>--json</c> check itself reads, so the listing cannot claim a command the flag would
    /// refuse; <paramref name="legacy"/> is frozen and short, and exists so that a plugin wanting
    /// model state does not have to hard-code the spelling of its version field.
    /// </summary>
    public static CapabilityData Describe(
        string binary,
        IReadOnlyList<string> enveloped,
        IReadOnlyList<string> legacy,
        IReadOnlyDictionary<string, string> tools)
    {
        ArgumentNullException.ThrowIfNull(enveloped);
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(tools);
        return new CapabilityData(
            binary,
            [
                .. enveloped.Select(name => Describe(name, MachineEnvelope.VersionField, tools)),
                .. legacy.Select(name => Describe(name, MachineEnvelope.LegacyVersionField, tools)),
            ]);
    }

    private static CommandCapability Describe(
        string name,
        string shape,
        IReadOnlyDictionary<string, string> tools) =>
        new(name, shape, tools.TryGetValue(name, out var tool) ? tool : null);
}
