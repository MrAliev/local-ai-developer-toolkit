using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Core.Resources;

namespace LocalAi.Cli;

/// <summary>What <c>localai read-image</c> was asked to look at.</summary>
public sealed record ReadImageRequest(
    string Question,
    IReadOnlyList<string> Images,
    LocalTaskProfile Profile,
    string? Model);

/// <summary>
/// Images on disk, read by a local vision model: a screenshot, a scanned page, a photographed
/// table, a diagram.
///
/// Same grammar as <c>ask</c> — the instruction first because it is required, the files after it
/// because a glob is how anybody names eight of them. The question has no default and does not
/// gain one here: "transcribe the error text" and "list every row" produce different answers, and
/// a default would quietly pick one.
/// </summary>
public static class ReadImageCommand
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReadImageRequest? request,
        out CommandRefusal? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        refusal = null;

        string? question = null;
        var images = new List<string>();
        var profile = LocalTaskProfile.VisualAnalysis;
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
                            CliText.OptionValueMissing(
                                "read-image",
                                argument,
                                CliUsage.ReadImage));
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
                            CliText.ProfileUnknown("read-image", value, ImageProfiles));
                        return false;
                    }

                    if (!LocalTasks.IsImageProfile(parsed))
                    {
                        refusal = new CommandRefusal(
                            "profile_not_supported",
                            CliText.ReadImageProfileNotSupported(value, ImageProfiles));
                        return false;
                    }

                    profile = parsed;
                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        refusal = new CommandRefusal(
                            "argument_unknown",
                            CliText.CommandUnknownArgument(
                                "read-image",
                                argument,
                                CliUsage.ReadImage));
                        return false;
                    }

                    // A first argument that is a bare image path is a forgotten question
                    // rather than a question about a file: `localai read-image shot.png` used to
                    // be told it needed an image, which is the one thing it had. A question that
                    // merely ends in a file name — "what is in shot.png" — has a space in it and
                    // stays a question.
                    if (question is null &&
                        !(LocalTasks.IsImageFile(argument) &&
                          !argument.Contains(' ', StringComparison.Ordinal)))
                    {
                        question = argument;
                        break;
                    }

                    // Pointing at a PDF is the likeliest mistake this command has, and left to
                    // the task it would arrive as one argument failure among many. Checked here
                    // with the same predicate and the same sentence, so it earns a code a program
                    // can act on.
                    if (!LocalTasks.IsImageFile(argument))
                    {
                        refusal = new CommandRefusal(
                            "file_not_image",
                            LocalLmText.NotAnImage(argument));
                        return false;
                    }

                    images.Add(argument);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            refusal = new CommandRefusal(
                "prompt_missing",
                CliText.ReadImageQuestionMissing(CliUsage.ReadImage));
            return false;
        }

        if (images.Count == 0)
        {
            refusal = new CommandRefusal(
                "source_missing",
                CliText.ReadImageNoImages(CliUsage.ReadImage));
            return false;
        }

        request = new ReadImageRequest(question, images, profile, model);
        return true;
    }

    /// <summary>
    /// Computed from the enum through the predicate the task itself checks, so this cannot name a
    /// profile the call would reject.
    /// </summary>
    private static string ImageProfiles =>
        string.Join(
            "|",
            Enum.GetValues<LocalTaskProfile>().Where(LocalTasks.IsImageProfile));
}
