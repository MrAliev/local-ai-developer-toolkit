using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>
/// Which of this binary's commands answer a program, and what they are called.
///
/// The envelope itself is <see cref="MachineEnvelope"/>, in Contracts, because `codesearch` needs
/// the same one and neither console should reference the other. What stays here is the part that
/// is about this binary: the list of commands that fill it, and how a command is named when it
/// does not.
/// </summary>
internal static class MachineOutput
{
    public const string Flag = MachineEnvelope.Flag;

    public const int Schema = MachineEnvelope.Schema;

    /// <summary>
    /// The language to answer in when the reader is a program: English, always.
    ///
    /// One line rather than a rule each command remembers, and it reaches the framework's own
    /// exception messages, which follow the same culture.
    ///
    /// It does not reach everything, and the exception proves the rule is worth keeping narrow:
    /// <c>Win32Exception</c> takes its words from the operating system rather than from a managed
    /// resource, so a Windows error arrives in the machine's language whatever this pins. Those
    /// paths are answered by giving them a code and a sentence of our own before they reach the
    /// guard — see the working-directory check in <c>GitClient</c>.
    /// </summary>
    public static string? Language(IReadOnlyList<string> arguments) =>
        MachineEnvelope.Language(Requested(arguments));

    /// <summary>
    /// A scan of the whole argument list is safe in this binary because no option's value could
    /// plausibly be the literal <c>--json</c>. That is not true of <c>codesearch search --query</c>,
    /// which uses <see cref="MachineEnvelope.RequestedAsOption"/> instead.
    /// </summary>
    public static bool Requested(IReadOnlyList<string> arguments) =>
        MachineEnvelope.RequestedAnywhere(arguments);

    /// <summary>The arguments a command sees, with the flag this class owns taken out.</summary>
    public static string[] Without(IReadOnlyList<string> arguments) =>
        MachineEnvelope.WithoutFlag(arguments);

    /// <summary>
    /// The commands that fill an envelope today. A flag whose promise held for some commands and
    /// not others would be worse than no flag: a caller cannot tell prose from an envelope without
    /// parsing, and parsing is the thing this exists to remove.
    ///
    /// `capabilities` is in here like any other enveloped command, which is what makes the flag
    /// reach it through the same pre-dispatch check and what puts it in its own answer without a
    /// special case.
    /// </summary>
    public static IReadOnlyList<string> Commands { get; } =
        ["repo status", "ask", "triage", "read-image", "translate", "capabilities"];

    /// <summary>
    /// The commands that printed a JSON shape of their own before the envelope existed, and still
    /// do. Frozen — `model` is parsed by <c>BrokerModelInstaller</c> and nothing will join it.
    ///
    /// Listed rather than hidden: leaving them out would leave exactly the gap `capabilities`
    /// exists to close, with a plugin wanting model state hard-coding the knowledge that this one
    /// spells its version field differently.
    /// </summary>
    private static readonly string[] Legacy =
        ["model status", "model pull", "model preflight"];

    /// <summary>
    /// The MCP tool that does the same job, for the commands that have one.
    ///
    /// This mapping exists nowhere else in the code, so it is a first copy rather than a second —
    /// and a test holds every name here against <c>McpToolNames</c>, which the server's own
    /// attributes are held against in turn.
    ///
    /// `model pull` is absent because there is no equivalent: <c>local_models_sync</c> queues the
    /// recommended missing set rather than a named model, and the server deliberately exposes no
    /// generic pull. `repo status` is absent for the same reason — nothing on the MCP surface
    /// answers whether a repository is connected.
    /// </summary>
    private static readonly Dictionary<string, string> Tools = new(StringComparer.Ordinal)
    {
        ["ask"] = "ask_local",
        ["triage"] = "triage_log",
        ["read-image"] = "read_image",
        ["translate"] = "translate_local",
        ["model status"] = "local_models_status",
        ["model preflight"] = "local_model_preflight",
    };

    /// <summary>
    /// What this binary can be driven to do, derived from the lists above rather than written out
    /// beside them. A listing that can disagree with the behaviour is worse than no listing.
    /// </summary>
    public static CapabilityData Capabilities() =>
        CommandCapabilities.Describe("localai", Commands, Legacy, Tools);

    public static bool Supports(string commandPath) =>
        Commands.Contains(commandPath, StringComparer.Ordinal);

    /// <summary>
    /// Which of the commands that fill an envelope this run is, or null for anything else.
    ///
    /// Matched against what exists rather than guessed from the leading words. Guessing was tried
    /// first — "the first two non-option tokens", on the grounds that every command path here is
    /// one or two words — and `ask` broke it immediately: its first positional argument is the
    /// reader's own instruction, so `localai ask "In one sentence: what is this?" --json` asked
    /// for a command whose name was that sentence, and was told the flag was unavailable.
    /// </summary>
    public static string? Enveloped(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Commands.FirstOrDefault(command =>
        {
            var words = command.Split(' ');
            return arguments.Count >= words.Length &&
                words.SequenceEqual(arguments.Take(words.Length), StringComparer.Ordinal);
        });
    }

    /// <summary>
    /// What to call a command that fills no envelope, for the refusal that says so: its first
    /// word, and only that. Everything after it may be anything at all — a prompt, a path, a
    /// query — and echoing it would put unvalidated input where a plugin looks for a name.
    /// </summary>
    public static string Named(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.FirstOrDefault(argument => !argument.StartsWith('-')) ?? string.Empty;
    }

    public static string Answer(string command, object data) =>
        MachineEnvelope.Answer(command, data);

    public static string Refusal(string command, string code, string message) =>
        MachineEnvelope.Refusal(command, code, message);
}
