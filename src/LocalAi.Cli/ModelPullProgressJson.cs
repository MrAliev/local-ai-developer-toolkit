using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>
/// The machine face of a download's progress: one object per line on standard error, and no
/// prose at all.
///
/// It exists because the installer runs this command as a child process and has to put the
/// figures into its own window, in its own language. Streaming the prose would put English into
/// a Russian installer whenever the two disagree about language, and would make a reviewer's
/// wording change a parser break. So the child emits numbers for a program and sentences for a
/// person, and only ever one of the two.
///
/// One face at a time also keeps the installer's guard meaningful: it refuses a child that
/// writes anything it did not expect on standard error, and that guard is what catches a binary
/// which has started printing warnings.
/// </summary>
public sealed class ModelPullProgressJson(TextWriter writer, string model) : ILocalRunObserver
{
    private string? last;

    public void Report(LocalRunStep step)
    {
        if (step is not ModelDownloadProgress download)
        {
            return;
        }

        var line = JsonSerializer.Serialize(
            new ModelPullProgressLine(
                1,
                "pull",
                model,
                download.Phase,
                download.Phase == "downloading" ? download.Completed : null,
                download.Phase == "downloading" ? download.Total : null,
                download.Phase == "other" ? download.Detail : null),
            LocalAiJson.Strict);

        // The client polls ten times a second and the broker republishes at most every two, so
        // most of what arrives here is the position already reported. A repeated line would be
        // indistinguishable from a stalled download that had moved.
        if (string.Equals(last, line, StringComparison.Ordinal))
        {
            return;
        }

        last = line;
        writer.WriteLine(line);
    }

    private sealed record ModelPullProgressLine(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("completedBytes")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? CompletedBytes,
        [property: JsonPropertyName("totalBytes")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? TotalBytes,
        [property: JsonPropertyName("status")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Status);
}
