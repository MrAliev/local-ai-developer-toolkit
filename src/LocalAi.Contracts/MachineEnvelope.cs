using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// One envelope, for every command in either console that answers a program rather than a person.
///
/// A plugin driving this product needs a shape it can parse and a version it can check, and it
/// needs both to be the same whichever binary answered. Since #308 the prose follows the reader —
/// right for a person and fatal for a parser — so the machine face is a separate one rather than
/// the same sentences pinned to a language.
///
/// It lives in Contracts because both consoles need it and neither should reference the other,
/// which is the same reason <see cref="Security.RedirectedSource"/> is here.
/// </summary>
public static class MachineEnvelope
{
    public const string Flag = "--json";

    /// <summary>
    /// The version of the envelope, not of the product and not of any command's <c>data</c>.
    /// Adding a field is not a change to it; removing, renaming or retyping one is.
    /// </summary>
    public const int Schema = 1;

    /// <summary>
    /// What that version field is called on the wire.
    ///
    /// Deliberately <c>schema</c> rather than <c>schemaVersion</c>: <c>localai model</c> and
    /// <c>codesearch evaluate</c> both printed shapes of their own before this one existed, both
    /// numbered 1, and the field name is the whole of what tells a plugin which of the three it
    /// holds. A constant rather than a spelling written out once in the record below, because
    /// <c>capabilities</c> reports it — and a listing naming a field the envelope does not write
    /// would be worse than no listing.
    /// </summary>
    public const string VersionField = "schema";

    /// <summary>
    /// What the two shapes that predate the envelope call the same field. Frozen: nothing new is
    /// built in that shape, and the pair exists so a plugin can be told which it is about to read
    /// rather than having to know.
    /// </summary>
    public const string LegacyVersionField = "schemaVersion";

    /// <summary>
    /// The language to answer in when the answer is for a program: English, always, or nothing to
    /// say when the reader is a person. Deciding and applying stay separate, as they do in
    /// <c>OutputCulture</c> — this is a pure answer, and changing the process is an entry point's
    /// business.
    /// </summary>
    public static string? Language(bool requested) => requested ? "en" : null;

    /// <summary>
    /// The flag, found anywhere in the arguments. Safe where no option's value could plausibly be
    /// the literal <c>--json</c>, which is true of <c>localai</c> and not of
    /// <c>codesearch search --query</c>.
    /// </summary>
    public static bool RequestedAnywhere(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Contains(Flag, StringComparer.Ordinal);
    }

    /// <summary>
    /// The flag as an option of its own, scanning past the value of any option before it — so a
    /// query that happens to be <c>--json</c> is a query.
    ///
    /// The hazard is not reachable through today's parser, which discards a value beginning with
    /// <c>--</c>. It is guarded anyway: the parser is the thing more likely to be fixed later, and
    /// a scan that was only accidentally correct would not survive that fix.
    /// </summary>
    public static bool RequestedAsOption(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], Flag, StringComparison.Ordinal))
            {
                return true;
            }

            if (arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
            }
        }

        return false;
    }

    /// <summary>The arguments a command sees, with the flag this class owns taken out.</summary>
    public static string[] WithoutFlag(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments
            .Where(argument => !string.Equals(argument, Flag, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// A command that did what it was asked. <c>ok</c> mirrors the exit code rather than the
    /// outcome: <c>localai sync</c> prints <c>REFUSED …</c> and exits 0 on purpose, and a plugin
    /// whose error path fired on that healthy run would be wrong about its own repository.
    /// </summary>
    public static string Answer(string command, object data) =>
        Write(new Envelope(Schema, command, true, data, null));

    public static string Refusal(string command, string code, string message) =>
        Write(new Envelope(Schema, command, false, null, new Failure(code, message)));

    private static string Write(Envelope envelope) =>
        JsonSerializer.Serialize(envelope, LocalAiJson.Strict);

    private sealed record Envelope(
        [property: JsonRequired, JsonPropertyName(VersionField), JsonPropertyOrder(0)]
        int Schema,
        [property: JsonRequired, JsonPropertyName("command"), JsonPropertyOrder(1)]
        string Command,
        [property: JsonRequired, JsonPropertyName("ok"), JsonPropertyOrder(2)]
        bool Ok,
        [property: JsonPropertyName("data"), JsonPropertyOrder(3),
                   JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        object? Data,
        [property: JsonPropertyName("error"), JsonPropertyOrder(4),
                   JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        Failure? Error);

    /// <summary>
    /// <c>code</c> is what a program branches on and never changes without a schema bump;
    /// <c>message</c> is what a person is shown, and no caller may parse it.
    /// </summary>
    private sealed record Failure(
        [property: JsonRequired, JsonPropertyName("code"), JsonPropertyOrder(0)]
        string Code,
        [property: JsonRequired, JsonPropertyName("message"), JsonPropertyOrder(1)]
        string Message);
}
