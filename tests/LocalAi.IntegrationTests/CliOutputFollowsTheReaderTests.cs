using LocalAi.Cli;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The CLI answers in the reader's language, and what a reader is meant to type does not move.
///
/// The first fact deliberately asserts no particular sentence. Pinning the words would make this
/// a second copy of the resource file, green for as long as the copy is kept up to date and
/// saying nothing about behaviour; that the two languages differ at all is the behaviour.
///
/// The second is the other half of the same rule, and it is the one that can regress quietly: a
/// translated option name reads almost right and leaves the reader with a command that does not
/// run.
/// </summary>
public sealed class CliOutputFollowsTheReaderTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-cli-language-" + Guid.NewGuid().ToString("N"));

    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void The_policy_report_is_not_the_same_text_in_both_languages()
    {
        Assert.Equal(0, Run("show"));
        var english = output.ToString();
        output.GetStringBuilder().Clear();

        using (TestCulture.Reading("ru"))
        {
            Assert.Equal(0, Run("show"));
        }

        Assert.NotEqual(english, output.ToString(), StringComparer.Ordinal);
    }

    [Fact]
    public void The_command_and_its_options_are_the_same_in_both_languages()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Equal(2, Run("nonsense"));

        foreach (var typed in new[]
                 {
                     "localai policy show",
                     "localai policy set --residency",
                     "--language <en|ru|system>",
                     "--idle-model-keep-alive-seconds",
                     "--update-check-interval-hours",
                     "RequireFullVram",
                     "AllowPartialOffload",
                     "AllowCpu",
                 })
        {
            Assert.Contains(typed, error.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The usage block's second column does not move between languages.
    ///
    /// The block is two columns that only mean anything together: a caption, then a description
    /// starting at a fixed offset. The captions are three literal option values and three prose
    /// captions, and a translated caption a few characters longer pushes the description column
    /// for all six rows at once — which no parity test can see, because the key is present in
    /// both files and both values are perfectly good sentences.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void The_usage_block_keeps_its_second_column(string language)
    {
        using var reading = TestCulture.Reading(language);

        Assert.Equal(2, Run("nonsense"));

        var captions = 0;
        foreach (var line in error.ToString()
                     .Split('\n')
                     .Select(line => line.TrimEnd('\r'))
                     .Where(line =>
                         line.StartsWith("  ", StringComparison.Ordinal) &&
                         line.Length > 2 &&
                         line[2] != ' '))
        {
            var description = line.IndexOf("  ", 2, StringComparison.Ordinal);
            Assert.True(description >= 0, $"A caption row carries no description: {line}");
            while (line[description] == ' ')
            {
                description++;
            }

            Assert.True(
                description == DescriptionColumn,
                $"The description starts at {description}, not {DescriptionColumn}: {line}");
            captions++;
        }

        Assert.Equal(6, captions);
    }

    /// <summary>
    /// Two spaces after the longest caption, which is <c>AllowPartialOffload</c> at nineteen
    /// characters. A Russian caption may be nineteen; it may not be twenty.
    /// </summary>
    private const int DescriptionColumn = 23;

    private int Run(params string[] args) =>
        PolicyCommand.Execute(args, root, output, error);
}
