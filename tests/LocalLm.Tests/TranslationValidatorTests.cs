using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class TranslationValidatorTests
{
    [Fact]
    public void Markdown_validator_preserves_structure_and_protected_tokens()
    {
        const string source =
            """
            # Heading

            - Item with `Code()` and [link](https://example.test/path)

            ```csharp
            Console.WriteLine("{value}");
            ```
            """;
        const string translated =
            """
            # Заголовок

            - Элемент с `Code()` и [ссылкой](https://example.test/path)

            ```csharp
            Console.WriteLine("{value}");
            ```
            """;

        var result = TranslationValidator.ValidateMarkdown(source, translated);

        Assert.True(result.Passed, result.Detail);
    }

    [Theory]
    [InlineData("# Heading\n\n`Code()`", "# Заголовок\n\n`Other()`")]
    [InlineData("# Heading\n\n- item", "Заголовок\n\n- элемент")]
    [InlineData("```\ncode\n```", "code")]
    public void Markdown_validator_rejects_structural_or_protected_token_loss(
        string source,
        string translated)
    {
        var result = TranslationValidator.ValidateMarkdown(source, translated);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Markdown_validator_rejects_a_fence_merged_into_a_heading()
    {
        const string source =
            "# Heading\r\n\r\n" +
            "```text\r\nvalue\r\n```\r\n";
        const string translated =
            "# Заголовок```text\r\nvalue\r\n```\r\n";

        var result = TranslationValidator.ValidateMarkdown(source, translated);

        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData("Hello", "")]
    [InlineData("English version", "Translate the following fragment from English to Russian.")]
    [InlineData("English version", "```\nprompt\n```")]
    [InlineData("One", "This response expanded far beyond a plausible short translation and contains unrelated instructions.")]
    public void Plain_validator_rejects_empty_leaked_or_anomalously_expanded_output(
        string source,
        string translated)
    {
        var result = TranslationValidator.ValidatePlain(source, translated);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Plain_validator_accepts_a_plausible_translation()
    {
        var result = TranslationValidator.ValidatePlain(
            "English version",
            "Английская версия");

        Assert.True(result.Passed, result.Detail);
    }
}
