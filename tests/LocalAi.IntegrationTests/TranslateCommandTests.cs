using System.Text.Json;
using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The fourth and last local-model command, and the only one whose output is a document rather
/// than an answer about one. That difference decides most of what is asserted here.
/// </summary>
public sealed class TranslateCommandTests
{
    /// <summary>
    /// Neither language has a default, and neither can be guessed. One code for both, because the
    /// remedy is the same and the caller knows which one they left out — the message names it.
    /// </summary>
    [Theory]
    [InlineData("--to", "Russian")]
    [InlineData("--from", "English")]
    public void One_language_on_its_own_is_not_enough(string option, string language)
    {
        Assert.False(TranslateCommand.TryParse(
            ["hello", option, language],
            piped: false,
            out _,
            out var refusal));

        Assert.Equal("language_missing", refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>And neither is neither.</summary>
    [Fact]
    public void With_no_language_at_all_it_is_the_same_refusal()
    {
        Assert.False(TranslateCommand.TryParse(["hello"], piped: false, out _, out var refusal));

        Assert.Equal("language_missing", refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// Names rather than codes, and the refusal shows one: the value is interpolated into the
    /// prompt as written, and the attribution paragraph picks its language by looking for
    /// "Russian" in the target — so `--to ru` does not fail, it quietly produces an English
    /// attribution on a Russian document. The example is the only warning a reader gets.
    /// </summary>
    [Fact]
    public void The_refusal_shows_language_names_because_a_code_would_not_fail_loudly()
    {
        TranslateCommand.TryParse(["hello"], piped: false, out _, out var refusal);

        Assert.Contains("--from English --to Russian", refusal!.Message, StringComparison.Ordinal);
    }

    /// <summary>The text is positional; a phrase typed at a prompt is the common case.</summary>
    [Fact]
    public void The_text_can_be_given_as_an_argument()
    {
        Assert.True(TranslateCommand.TryParse(
            ["hello there", "--from", "English", "--to", "Russian"],
            piped: false,
            out var request,
            out _));

        Assert.Equal("hello there", request!.Text, StringComparer.Ordinal);
        Assert.False(request.FromStandardInput);
        Assert.False(request.Markdown);
        Assert.Null(request.OutputPath);
    }

    /// <summary>A document arrives on standard input, exactly as `triage` takes a log.</summary>
    [Fact]
    public void A_document_arrives_on_standard_input()
    {
        Assert.True(TranslateCommand.TryParse(
            ["--from", "English", "--to", "Russian", "--markdown"],
            piped: true,
            out var request,
            out _));

        Assert.True(request!.FromStandardInput);
        Assert.True(request.Markdown);
    }

    /// <summary>Nothing given and nothing piped: refuse rather than block on a terminal.</summary>
    [Fact]
    public void Nothing_to_translate_is_refused_rather_than_waited_for()
    {
        Assert.False(TranslateCommand.TryParse(
            ["--from", "English", "--to", "Russian"],
            piped: false,
            out _,
            out var refusal));

        Assert.Equal("source_missing", refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// `--out` exists because this command's answer is the artifact. Redirecting it would write a
    /// file wrapped in provenance markers, which is useless as a document — and dropping the
    /// markers for this one command would make the console face weaker than the MCP face.
    /// </summary>
    [Fact]
    public void The_document_has_a_place_to_be_written_that_is_not_a_redirect()
    {
        Assert.True(TranslateCommand.TryParse(
            ["-", "--from", "English", "--to", "Russian", "--out", @"C:\readme.ru.md"],
            piped: true,
            out var request,
            out _));

        Assert.Equal(@"C:\readme.ru.md", request!.OutputPath, StringComparer.Ordinal);
    }

    /// <summary>
    /// The pair of `--out`. This is the one command whose input is a document as often as its
    /// output is one, and a pipe is not a way to hand one over: standard input decodes with the
    /// console's input code page, which this binary never sets, so a UTF-8 document arrives at
    /// the model already mangled. A named file is read as UTF-8 with BOM detection.
    /// </summary>
    [Fact]
    public void A_document_can_be_named_rather_than_piped()
    {
        Assert.True(TranslateCommand.TryParse(
            ["--in", @"C:\readme.md", "--from", "English", "--to", "Russian"],
            piped: false,
            out var request,
            out _));

        Assert.Equal(@"C:\readme.md", request!.InputPath, StringComparer.Ordinal);
        Assert.Null(request.Text);
        Assert.False(request.FromStandardInput);
    }

    /// <summary>
    /// `--in -` is standard input, so a wrapper that has either can pass it without branching —
    /// the plugins planned on top of this console are exactly that wrapper.
    /// </summary>
    [Fact]
    public void A_dash_after_the_option_is_standard_input()
    {
        Assert.True(TranslateCommand.TryParse(
            ["--in", "-", "--from", "English", "--to", "Russian"],
            piped: true,
            out var request,
            out _));

        Assert.True(request!.FromStandardInput);
        Assert.Null(request.InputPath);
    }

    /// <summary>
    /// One source, whichever two were named. `--in` is new, so it is strict from its first
    /// release at no compatibility cost; the older `"text" -` pair keeps its settled behaviour,
    /// where a script may already rely on the text winning.
    /// </summary>
    [Theory]
    [InlineData(new[] { "hello", "--in", @"C:\readme.md" }, "a text and a file")]
    [InlineData(new[] { "-", "--in", @"C:\readme.md" }, "standard input and a file")]
    [InlineData(new[] { "--in", @"C:\a.md", "--in", @"C:\b.md" }, "two files")]
    public void One_source_or_the_other_never_both(string[] source, string pair)
    {
        Assert.False(
            TranslateCommand.TryParse(
                [.. source, "--from", "English", "--to", "Russian"],
                piped: true,
                out _,
                out var refusal),
            pair + " names two sources, and only one may be filled");

        Assert.Equal("source_ambiguous", refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// Translating in place destroys the original and there is no undo. Compared resolved, so
    /// two spellings of one file are still one file — and resolving touches no disk, which is
    /// what keeps this check in the parser.
    /// </summary>
    [Fact]
    public void The_translation_may_not_overwrite_its_own_source()
    {
        Assert.False(TranslateCommand.TryParse(
            [
                "--in", @"C:\docs\readme.md",
                "--from", "English",
                "--to", "Russian",
                "--out", @"C:\docs\..\docs\readme.md",
            ],
            piped: false,
            out _,
            out var refusal));

        Assert.Equal("output_is_source", refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// A refusal that omits a route sends the reader down a worse one, so the sentence that
    /// fires when nothing was given has to name all three.
    /// </summary>
    [Fact]
    public void The_refusal_for_nothing_at_all_names_every_way_in()
    {
        TranslateCommand.TryParse(
            ["--from", "English", "--to", "Russian"],
            piped: false,
            out _,
            out var refusal);

        Assert.Contains("--in", refusal!.Message, StringComparison.Ordinal);
    }

    /// <summary>Every option that takes a value refuses the same way when it is left off.</summary>
    [Theory]
    [InlineData("--from", "from_value_missing")]
    [InlineData("--to", "to_value_missing")]
    [InlineData("--out", "out_value_missing")]
    [InlineData("--in", "in_value_missing")]
    public void An_option_without_its_value_names_itself(string option, string code)
    {
        Assert.False(TranslateCommand.TryParse(
            ["text", "--from", "English", "--to", "Russian", option],
            piped: false,
            out _,
            out var refusal));

        Assert.Equal(code, refusal!.Code, StringComparer.Ordinal);
    }

    /// <summary>
    /// Three token figures where the others have one, and all three say they are estimates: three
    /// bare numbers would invite three false precisions instead of one.
    /// </summary>
    [Fact]
    public void The_envelope_carries_all_three_token_figures_as_estimates()
    {
        var json = MachineOutput.Answer(
            "translate",
            new TranslationData(
                "перевод", "translate:stdin", "qwen3.5:9b", "None", 180, 9400, 1200, 8600, 900));

        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal(1200, data.GetProperty("savedTokensEstimate").GetInt32());
        Assert.Equal(8600, data.GetProperty("localTokensProcessedEstimate").GetInt32());
        Assert.Equal(900, data.GetProperty("netContextTokensSavedEstimate").GetInt32());

        // Translation chunks the whole input and drops nothing, so a constant `false` here would
        // imply a concept that does not apply to this command.
        Assert.False(data.TryGetProperty("truncated", out _));
    }

    /// <summary>Marked in the usage, filling an envelope, and offering no model override.</summary>
    [Fact]
    public void The_usage_line_offers_only_what_the_task_actually_takes()
    {
        Assert.Contains("[--json]", CliUsage.Translate, StringComparison.Ordinal);
        Assert.Contains("--out", CliUsage.Translate, StringComparison.Ordinal);

        // The three ways in are one slot and only one may be filled, which is the fact worth
        // showing. `repo status` already spells an alternation this way in this same file.
        Assert.Contains("[text|-|--in file]", CliUsage.Translate, StringComparison.Ordinal);
        Assert.Contains(CliUsage.Translate, CliUsage.Text, StringComparison.Ordinal);
        Assert.True(MachineOutput.Supports("translate"));

        // `TranslateAsync` has no override parameter, so offering one would be a lie.
        Assert.DoesNotContain("--model", CliUsage.Translate, StringComparison.Ordinal);
    }
}
