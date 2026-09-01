using System.Globalization;
using LocalAi.Installer.ViewModels;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The installer speaks English only, while every document in this repository comes in both
/// languages (#258). The choice has to survive the run that made it — somebody who picked
/// Russian to install should not be asked again to uninstall — and it has to have an answer
/// before anybody has chosen anything.
/// </summary>
public sealed class InstallerLanguageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-language-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ru-RU", InstallerLanguage.Russian)]
    [InlineData("ru", InstallerLanguage.Russian)]
    [InlineData("en-GB", InstallerLanguage.English)]
    [InlineData("de-DE", InstallerLanguage.English)]
    [InlineData("", InstallerLanguage.English)]
    public void With_nothing_chosen_the_operating_system_decides(string culture, InstallerLanguage expected)
    {
        var store = new InstallerLanguageStore(_root);

        Assert.Equal(expected, store.Read(new CultureInfo(culture)));
    }

    [Fact]
    public void A_choice_outlives_the_run_that_made_it()
    {
        new InstallerLanguageStore(_root).Write(InstallerLanguage.Russian);

        Assert.Equal(
            InstallerLanguage.Russian,
            new InstallerLanguageStore(_root).Read(new CultureInfo("en-US")));
    }

    /// <summary>
    /// A file nobody can read is not a reason to fail: the installer has a perfectly good
    /// answer without it, and refusing to start over a preferences file would be absurd.
    /// </summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"language\":\"klingon\"}")]
    public void An_unusable_file_falls_back_to_the_operating_system(string content)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ui-language.json"), content);

        Assert.Equal(
            InstallerLanguage.Russian,
            new InstallerLanguageStore(_root).Read(new CultureInfo("ru-RU")));
    }

    /// <summary>
    /// Deliberately not the runtime root: that directory is validated against an exact list of
    /// names on every install, and a stray file in it once broke every installation after the
    /// download. The logs directory is already the installer's own.
    /// </summary>
    [Fact]
    public void The_choice_is_kept_beside_the_installer_logs_rather_than_in_the_runtime()
    {
        Assert.EndsWith(
            Path.Combine("LocalAi-installer-logs", "ui-language.json"),
            InstallerLanguageStore.DefaultPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writing must not fail the run either. A read-only or missing directory means the choice
    /// is not remembered, which is a smaller loss than an installer that will not start.
    /// </summary>
    [Fact]
    public void A_choice_that_cannot_be_written_is_not_an_error()
    {
        var store = new InstallerLanguageStore(Path.Combine(_root, "nested", "deeper"));

        store.Write(InstallerLanguage.Russian);

        Assert.Equal(InstallerLanguage.Russian, store.Read(new CultureInfo("en-US")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
