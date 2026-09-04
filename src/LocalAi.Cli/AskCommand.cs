using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.Cli;

/// <summary>What <c>localai ask</c> was asked to do.</summary>
public sealed record AskRequest(
    string Prompt,
    IReadOnlyList<string> Files,
    LocalTaskProfile Profile,
    string? Model);

/// <summary>
/// A mechanical task over known files, run on a local model: summarise this, list every method
/// that does X, collect the TODOs.
///
/// The same <c>LocalTasks.AskAsync</c> the <c>ask_local</c> MCP tool calls. This is a second
/// entry point to it, for a person at a prompt and for an agent whose MCP server is not running —
/// the fallback this product's own instruction block tells every machine to use.
/// </summary>
public static class AskCommand
{
    /// <summary>
    /// The instruction is positional because it is required and cannot be defaulted; the files
    /// are positional and variadic because a shell glob is how anybody names sixty of them, and
    /// sixty-four is the limit <c>LocalTasks</c> enforces.
    /// </summary>
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out AskRequest? request,
        out CommandRefusal? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        refusal = null;

        string? prompt = null;
        var files = new List<string>();
        var profile = LocalTaskProfile.ShortSummary;
        string? model = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--profile":
                case "--model":
                    if (index + 1 >= arguments.Count)
                    {
                        refusal = new CommandRefusal(
                            argument.TrimStart('-') + "_value_missing",
                            CliText.OptionValueMissing("ask", argument, CliUsage.Ask));
                        return false;
                    }

                    var value = arguments[++index];
                    if (argument == "--model")
                    {
                        model = value;
                        break;
                    }

                    if (!Enum.TryParse<LocalTaskProfile>(value, ignoreCase: true, out var parsed) ||
                        !Enum.IsDefined(parsed))
                    {
                        refusal = new CommandRefusal(
                            "profile_unknown",
                            CliText.ProfileUnknown("ask", value, TextChatProfiles));
                        return false;
                    }

                    // A profile that exists but routes to an image or embedding model is a
                    // different mistake from a word that is not a profile at all, and the reader
                    // who made each one needs a different sentence.
                    if (!LocalTasks.IsTextChatProfile(parsed))
                    {
                        refusal = new CommandRefusal(
                            "profile_not_supported",
                            CliText.AskProfileNotSupported(value, TextChatProfiles));
                        return false;
                    }

                    profile = parsed;
                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        refusal = new CommandRefusal(
                            "argument_unknown",
                            CliText.CommandUnknownArgument("ask", argument, CliUsage.Ask));
                        return false;
                    }

                    if (prompt is null)
                    {
                        prompt = argument;
                        break;
                    }

                    files.Add(argument);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            refusal = new CommandRefusal(
                "prompt_missing",
                CliText.AskPromptMissing(CliUsage.Ask));
            return false;
        }

        request = new AskRequest(prompt, files, profile, model);
        return true;
    }

    /// <summary>
    /// Computed from the enum through the same predicate the task itself checks, so this cannot
    /// name a profile the call would reject.
    /// </summary>
    private static string TextChatProfiles =>
        string.Join(
            "|",
            Enum.GetValues<LocalTaskProfile>().Where(LocalTasks.IsTextChatProfile));
}
