using LocalAi.Cli;
using LocalAi.Contracts.Localization;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The command that sets the language, because the operating system's answer is a default rather
/// than a verdict: somebody working in English on a Russian machine has no other way to say so.
///
/// It sits under `policy` with residency and the update check rather than in a command of its
/// own, because it is the same kind of thing — one installation-wide setting, read by every
/// process that prints.
/// </summary>
public sealed class PolicyCommandLanguageTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-language-cli-" + Guid.NewGuid().ToString("N"));

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
    public void A_fresh_machine_reports_that_it_follows_the_system()
    {
        Assert.Equal(0, Run("show"));
        Assert.Contains("language: system", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_language_stores_it_and_says_so()
    {
        Assert.Equal(0, Run("set", "--language", "ru"));

        Assert.Equal("ru", new OutputLanguageStore(root).Read());
        Assert.Contains("language: ru", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_choice_is_what_show_reports_afterwards()
    {
        Run("set", "--language", "ru");
        output.GetStringBuilder().Clear();

        Assert.Equal(0, Run("show"));
        Assert.Contains("language: ru", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_the_system_again_clears_the_choice()
    {
        Run("set", "--language", "ru");

        Assert.Equal(0, Run("set", "--language", "system"));

        Assert.Null(new OutputLanguageStore(root).Read());
    }

    /// <summary>
    /// A language with no resources behind it must be refused at the moment somebody asks for
    /// it. Accepting it and answering in English anyway is the failure that reports nothing.
    /// </summary>
    [Fact]
    public void A_language_the_product_does_not_speak_is_refused_rather_than_stored()
    {
        Assert.NotEqual(0, Run("set", "--language", "klingon"));

        Assert.Null(new OutputLanguageStore(root).Read());
        Assert.Contains("klingon", error.ToString(), StringComparison.Ordinal);
    }

    private int Run(params string[] args) =>
        PolicyCommand.Execute(args, root, output, error);
}
