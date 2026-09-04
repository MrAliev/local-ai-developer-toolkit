using LocalAi.Cli.Resources;

namespace LocalAi.Cli;

/// <summary>What <c>localai translate</c> was asked to translate, and where to put it.</summary>
public sealed record TranslateRequest(
    string? Text,
    bool FromStandardInput,
    string From,
    string To,
    bool Markdown,
    string? OutputPath);

/// <summary>
/// The one command here whose answer is the artifact rather than a statement about one.
///
/// That is why <c>--out</c> exists. Redirecting the answer would write a file wrapped in the
/// provenance markers every redirected answer carries, which is useless as a document; dropping
/// the markers for this one command would make the console face weaker than the MCP face, which
/// wraps unconditionally; and a <c>--raw</c> flag would be a flag that turns a safety boundary
/// off, which is the kind that gets copied into scripts. <c>--out</c> says "this is a document,
/// write it here" and leaves the rule uniform across all four commands.
///
/// No <c>--model</c>: <c>LocalTasks.TranslateAsync</c> has no override parameter, and a usage
/// line offering one would be a lie.
/// </summary>
public static class TranslateCommand
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        bool piped,
        out TranslateRequest? request,
        out CommandRefusal? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        refusal = null;

        string? text = null;
        var fromStandardInput = false;
        string? from = null;
        string? to = null;
        var markdown = false;
        string? output = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "-")
            {
                fromStandardInput = true;
                continue;
            }

            switch (argument)
            {
                case "--markdown":
                    markdown = true;
                    break;

                case "--from":
                case "--to":
                case "--out":
                    if (index + 1 >= arguments.Count)
                    {
                        refusal = new CommandRefusal(
                            argument.TrimStart('-') + "_value_missing",
                            CliText.OptionValueMissing(
                                "translate",
                                argument,
                                CliUsage.Translate));
                        return false;
                    }

                    var value = arguments[++index];
                    switch (argument)
                    {
                        case "--from":
                            from = value;
                            break;
                        case "--to":
                            to = value;
                            break;
                        default:
                            output = value;
                            break;
                    }

                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        refusal = new CommandRefusal(
                            "argument_unknown",
                            CliText.CommandUnknownArgument(
                                "translate",
                                argument,
                                CliUsage.Translate));
                        return false;
                    }

                    if (text is not null)
                    {
                        refusal = new CommandRefusal(
                            "source_ambiguous",
                            CliText.TranslateOneText(CliUsage.Translate));
                        return false;
                    }

                    text = argument;
                    break;
            }
        }

        // Refused here rather than left to ThrowIfNullOrWhiteSpace inside the task, which would
        // arrive as a bare argument failure with a vaguer sentence and the wrong code.
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            refusal = new CommandRefusal(
                "language_missing",
                CliText.TranslateLanguageMissing(CliUsage.Translate));
            return false;
        }

        if (text is not null)
        {
            request = new TranslateRequest(text, false, from, to, markdown, output);
            return true;
        }

        if (!fromStandardInput && !piped)
        {
            refusal = new CommandRefusal(
                "source_missing",
                CliText.TranslateNoSource(CliUsage.Translate));
            return false;
        }

        request = new TranslateRequest(null, true, from, to, markdown, output);
        return true;
    }
}
