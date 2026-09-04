using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>
/// One envelope, for every command that answers a program rather than a person.
///
/// A plugin driving this console needs a shape it can parse and a version it can check, and it
/// needs both to be the same on every machine. Since #308 the prose follows the reader — which is
/// right for a person and fatal for a parser — so the machine face is a separate one rather than
/// the same sentences in a fixed language.
///
/// The alternative, a JSON shape per command, was weighed and rejected: every caller would write
/// the same error handling again, and there would be no single place to version. Here a command
/// owns only what goes inside <c>data</c>.
/// </summary>
internal static class MachineOutput
{
    public const string Flag = "--json";

    /// <summary>
    /// The version of the envelope, not of the product and not of any command's <c>data</c>.
    /// Adding a field is not a change to it; removing, renaming or retyping one is.
    ///
    /// Deliberately <c>schema</c> rather than <c>schemaVersion</c>: <c>localai model</c> has
    /// printed an envelope of its own since before this one existed, also numbered 1, and the
    /// field name is what tells a plugin which of the two it is holding — see
    /// <c>ModelCommandContracts.cs</c>, which is an older and unrelated shape. Do not unify them:
    /// <c>BrokerModelInstaller</c> parses that one and checks its version.
    /// </summary>
    public const int Schema = 1;

    /// <summary>
    /// The language to answer in when the answer is for a program: English, always, or nothing
    /// to say when the reader is a person.
    ///
    /// Deciding and applying are separate here for the reason they are separate in
    /// <see cref="LocalAi.Contracts.Localization.OutputCulture"/>: this is a pure answer anything
    /// may ask for, and changing the process is something only an entry point may do.
    ///
    /// One line rather than a rule each command remembers, and it reaches the framework's own
    /// exception messages, which follow the same culture.
    ///
    /// It does not reach everything, and the exception proves the rule is worth keeping
    /// narrow: <c>Win32Exception</c> takes its words from the operating system rather than
    /// from a managed resource, so a Windows error arrives in the machine's language whatever
    /// this pins. Those paths are answered by giving them a code and a sentence of our own
    /// before they reach the guard — see the working-directory check in <c>GitClient</c>.
    /// </summary>
    public static string? Language(IReadOnlyList<string> arguments) =>
        Requested(arguments) ? "en" : null;

    public static bool Requested(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Contains(Flag, StringComparer.Ordinal);
    }

    /// <summary>
    /// The arguments a command sees, with the flag this class owns taken out — every command
    /// here refuses what it does not recognise, and this one is not theirs to recognise.
    ///
    /// A scan of the whole argument list is safe in this binary because no option's value could
    /// plausibly be the literal <c>--json</c>. That is not true of <c>codesearch search --query</c>,
    /// where it is an ordinary thing to search for; when the flag reaches that binary the scan
    /// there has to skip an option's value.
    /// </summary>
    public static string[] Without(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments
            .Where(argument => !string.Equals(argument, Flag, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// The commands that fill an envelope today. A flag whose promise held for some commands and
    /// not others would be worse than no flag: a caller cannot tell prose from an envelope
    /// without parsing, and parsing is the thing this exists to remove.
    /// </summary>
    private static readonly string[] Enveloped = ["repo status"];

    public static bool Supports(string commandPath) =>
        Enveloped.Contains(commandPath, StringComparer.Ordinal);

    /// <summary>
    /// The command as typed, without its options: <c>repo status</c>, <c>hooks install</c>,
    /// <c>prune</c>. The leaf alone would collide — three groups have a <c>status</c>.
    ///
    /// Two words at most, and not an arbitrary limit: every command path this binary answers to
    /// is one or two, <c>bootstrap --dry-run</c> included, whose path is one because the rest is
    /// an option. It bounds an echo of what somebody typed without a character count.
    /// </summary>
    public static string CommandPath(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(
            ' ',
            arguments.TakeWhile(argument => !argument.StartsWith('-')).Take(2));
    }

    /// <summary>
    /// A command that did what it was asked. <c>ok</c> mirrors the exit code rather than the
    /// outcome: <c>sync</c> prints <c>REFUSED …</c> and exits 0 on purpose, and a plugin whose
    /// error path fired on that healthy run would be wrong about its own repository.
    /// </summary>
    public static string Answer(string command, object data) =>
        Write(new Envelope(Schema, command, true, data, null));

    public static string Refusal(string command, string code, string message) =>
        Write(new Envelope(Schema, command, false, null, new Failure(code, message)));

    private static string Write(Envelope envelope) =>
        JsonSerializer.Serialize(envelope, LocalAiJson.Strict);

    private sealed record Envelope(
        [property: JsonRequired, JsonPropertyName("schema"), JsonPropertyOrder(0)]
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
