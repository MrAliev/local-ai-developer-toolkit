using LocalAi.Cli.Resources;

namespace LocalAi.Cli;

/// <summary>What <c>localai translate</c> was asked to translate, and where to put it.</summary>
public sealed record TranslateRequest(
    string? Text,
    bool FromStandardInput,
    string From,
    string To,
    bool Markdown,
    string? OutputPath,
    string? InputPath);

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
/// <c>--in &lt;file&gt;</c> is its pair, and the only correct way to hand this command a document:
/// standard input is decoded with the console's input code page, which this binary never sets, so
/// a UTF-8 file piped in arrives at the model already mangled. Opening it is
/// <see cref="TranslateSource"/>'s job rather than this parser's — nothing here touches the disk,
/// which is what lets every refusal below be tested with a literal path.
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
        var dashGiven = false;
        string? from = null;
        string? to = null;
        var markdown = false;
        string? output = null;
        string? input = null;
        var inGiven = 0;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "-")
            {
                fromStandardInput = true;
                dashGiven = true;
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
                case "--in":
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
                        case "--out":
                            output = value;
                            break;
                        default:
                            // `--in -` is standard input, so a wrapper holding either can pass it
                            // without branching. It names the same source a bare `-` does, which
                            // is why it sets no path and the two together are not a conflict.
                            inGiven++;
                            if (value == "-")
                            {
                                fromStandardInput = true;
                            }
                            else
                            {
                                input = value;
                            }

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

        // One source, whichever two were named. `--in` is new, so it is strict from its first
        // release at no compatibility cost; the older pair of a text and a bare `-` keeps the
        // settled behaviour where the text wins and standard input is never read, because a
        // script may already sit on it.
        if (inGiven > 1 || (input is not null && (text is not null || dashGiven)))
        {
            refusal = new CommandRefusal(
                "source_ambiguous",
                CliText.TranslateOneSource(CliUsage.Translate));
            return false;
        }

        // Translating in place destroys the original and there is no undo. Compared resolved, so
        // two spellings of one file are still one file — and resolving touches no disk, which is
        // what keeps this check in the parser.
        if (input is not null && output is not null)
        {
            var resolvedInput = Path.GetFullPath(input);
            if (string.Equals(
                    resolvedInput,
                    Path.GetFullPath(output),
                    StringComparison.OrdinalIgnoreCase))
            {
                refusal = new CommandRefusal(
                    "output_is_source",
                    CliText.TranslateOutputIsSource(resolvedInput));
                return false;
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
            request = new TranslateRequest(text, false, from, to, markdown, output, null);
            return true;
        }

        if (input is not null)
        {
            request = new TranslateRequest(null, false, from, to, markdown, output, input);
            return true;
        }

        if (!fromStandardInput && !piped)
        {
            refusal = new CommandRefusal(
                "source_missing",
                CliText.TranslateNoSource(CliUsage.Translate));
            return false;
        }

        request = new TranslateRequest(null, true, from, to, markdown, output, null);
        return true;
    }
}
