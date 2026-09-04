using LocalAi.Cli;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.IntegrationTests;

/// <summary>
/// Reading images from a terminal: the third command to reach a local model, and the one whose
/// most likely mistake is pointing at a file that is not an image.
/// </summary>
public sealed class ReadImageCommandTests
{
    /// <summary>
    /// The question has no default in the task and should not gain one here: "transcribe the
    /// error text" and "list every row of the table" produce different answers, and a defaulted
    /// question would quietly pick one of them.
    /// </summary>
    [Fact]
    public void Read_image_without_a_question_says_what_the_form_is()
    {
        Assert.False(ReadImageCommand.TryParse([@"C:\shot.png"], out _, out var refusal));

        Assert.Equal("prompt_missing", refusal!.Code, StringComparer.Ordinal);
        Assert.Contains("localai read-image", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule that tells a forgotten question from a question about a file: a bare path has no
    /// spaces in it and a question does. Without the space test, "what is in shot.png" would be
    /// taken for an image and the reader told their question was missing.
    /// </summary>
    [Fact]
    public void A_question_that_ends_in_a_file_name_is_still_a_question()
    {
        Assert.True(ReadImageCommand.TryParse(
            ["what is in shot.png", @"C:\real.png"],
            out var request,
            out _));

        Assert.Equal("what is in shot.png", request!.Question, StringComparer.Ordinal);
        Assert.Equal([@"C:\real.png"], request.Images);
    }

    /// <summary>Nothing to read is the same failure `triage` has when nothing was piped.</summary>
    [Fact]
    public void Read_image_with_no_image_refuses_with_the_code_that_means_nothing_to_work_on()
    {
        Assert.False(ReadImageCommand.TryParse(["transcribe the error"], out _, out var refusal));

        Assert.Equal("source_missing", refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// Pointing at a PDF is the likeliest mistake this command has. Left to the task it would
    /// arrive as a bare argument failure and be reported as `input_rejected`, which tells a
    /// program nothing it can act on — so the parser checks the extension itself, with the same
    /// sentence the task would have used.
    /// </summary>
    [Fact]
    public void A_file_that_is_not_an_image_is_named_and_gets_a_code_of_its_own()
    {
        Assert.False(ReadImageCommand.TryParse(
            ["transcribe", @"C:\report.pdf"],
            out _,
            out var refusal));

        Assert.Equal("file_not_image", refusal!.Code, StringComparer.Ordinal);
        Assert.Contains("report.pdf", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A profile that exists but is not for images, refused the way `ask` refuses a profile that
    /// is not for text — same shape, same computed set, so two commands do not answer the same
    /// class of mistake differently.
    /// </summary>
    [Fact]
    public void A_profile_meant_for_text_is_refused_with_the_set_that_would_work()
    {
        Assert.False(ReadImageCommand.TryParse(
            ["transcribe", @"C:\shot.png", "--profile", "ShortSummary"],
            out _,
            out var refusal));

        Assert.Equal("profile_not_supported", refusal!.Code, StringComparer.Ordinal);
        Assert.Contains("VisualAnalysis", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Ocr", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortSummary|", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A word that is not a profile at all is the other mistake, with the other code.</summary>
    [Fact]
    public void A_word_that_is_not_a_profile_is_a_different_failure()
    {
        Assert.False(ReadImageCommand.TryParse(
            ["transcribe", @"C:\shot.png", "--profile", "Visual"],
            out _,
            out var refusal));

        Assert.Equal("profile_unknown", refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>The default is the one the MCP tool defaults to, and the six extensions it takes.</summary>
    [Theory]
    [InlineData("shot.png")]
    [InlineData("scan.JPEG")]
    [InlineData("photo.webp")]
    public void The_images_are_taken_as_they_are_written_with_the_visual_profile_by_default(
        string file)
    {
        Assert.True(ReadImageCommand.TryParse(
            ["list every row", @"C:\" + file],
            out var request,
            out _));

        Assert.Equal(LocalTaskProfile.VisualAnalysis, request!.Profile);
        Assert.Equal("list every row", request.Question, StringComparer.Ordinal);
        Assert.Equal([@"C:\" + file], request.Images);
    }

    /// <summary>
    /// The set of image profiles lives in one place, so a refusal cannot name a profile the call
    /// would reject — the reason `IsTextChatProfile` was made public for `ask`.
    /// </summary>
    [Theory]
    [InlineData(LocalTaskProfile.VisualAnalysis, true)]
    [InlineData(LocalTaskProfile.Ocr, true)]
    [InlineData(LocalTaskProfile.ImageTranslation, true)]
    [InlineData(LocalTaskProfile.ShortSummary, false)]
    [InlineData(LocalTaskProfile.VectorEmbedding, false)]
    public void The_predicate_the_refusal_uses_is_the_one_the_task_checks(
        LocalTaskProfile profile,
        bool image)
    {
        Assert.Equal(image, LocalTasks.IsImageProfile(profile));
    }

    /// <summary>
    /// Marked in the usage, answering the flag: the refusal for `--json` elsewhere sends readers
    /// to those marks, so a mark without an envelope is a dead end.
    /// </summary>
    [Fact]
    public void It_is_marked_in_the_usage_and_fills_an_envelope()
    {
        Assert.Contains("[--json]", CliUsage.ReadImage, StringComparison.Ordinal);
        Assert.Contains(CliUsage.ReadImage, CliUsage.Text, StringComparison.Ordinal);
        Assert.True(MachineOutput.Supports("read-image"));
    }
}
