using LocalAi.Cli.Resources;

namespace LocalAi.Cli;

/// <summary>
/// The listing exists for a program, and <c>--json</c> is how a program asks.
///
/// Making the flag required rather than optional is the whole design of this command: a prose face
/// would be a second inventory beside the usage block, and a second inventory is what drifts. The
/// block already carries the syntax, the options and the <c>[--json]</c> marks; this carries the
/// part a parser needs and nothing else.
/// </summary>
internal static class CapabilitiesCommand
{
    /// <summary>
    /// Null when the run may answer, otherwise what to say instead.
    ///
    /// <c>json_required</c> is the one code in this binary that can never appear inside an
    /// envelope, because the condition that produces it is the absence of the envelope. A caller
    /// branching on codes will never see it; a person reading stderr is exactly who it is for.
    /// </summary>
    public static CommandRefusal? Refused(IReadOnlyList<string> arguments, bool machineReadable)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!machineReadable)
        {
            return new CommandRefusal("json_required", CliText.CapabilitiesNeedsJson);
        }

        // Nothing but the command word, the flag having been taken out before dispatch. An
        // argument quietly ignored leaves a caller believing something happened, which is the
        // same reason `repo status` refuses what it does not understand.
        return arguments.Skip(1).FirstOrDefault() is { } unknown
            ? new CommandRefusal(
                "argument_unknown",
                CliText.CommandUnknownArgument("capabilities", unknown, CliUsage.Capabilities))
            : null;
    }
}
