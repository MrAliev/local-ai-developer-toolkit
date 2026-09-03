using System.Globalization;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Two preferences, one document. The language arrived first (#258) in its own file; the theme
/// (#259) is the same kind of answer — chosen once, remembered, and defaulting to what the
/// operating system already says — so they share a document rather than accumulating a file
/// each beside the installer's logs.
///
/// Neither may ever fail a run. An unreadable file, a name no longer recognised, a directory
/// that cannot be created: each of them has a perfectly good answer already, which is what the
/// operating system says.
/// </summary>
public sealed class InstallerPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-preferences-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ru-RU", InstallerLanguage.Russian)]
    [InlineData("ru", InstallerLanguage.Russian)]
    [InlineData("en-GB", InstallerLanguage.English)]
    [InlineData("de-DE", InstallerLanguage.English)]
    [InlineData("", InstallerLanguage.English)]
    public void With_no_language_chosen_the_operating_system_decides(
        string culture,
        InstallerLanguage expected)
    {
        var store = new InstallerPreferencesStore(_root);

        Assert.Equal(expected, store.ReadLanguage(new CultureInfo(culture)));
    }

    [Fact]
    public void A_language_outlives_the_run_that_chose_it()
    {
        new InstallerPreferencesStore(_root).WriteLanguage(InstallerLanguage.Russian);

        Assert.Equal(
            InstallerLanguage.Russian,
            new InstallerPreferencesStore(_root).ReadLanguage(new CultureInfo("en-US")));
    }

    /// <summary>
    /// "System" rather than light: a person who has already told Windows which they prefer has
    /// answered this question, and asking it again is the installer not listening.
    /// </summary>
    [Fact]
    public void With_no_theme_chosen_the_installer_follows_the_system()
    {
        Assert.Equal(InstallerTheme.System, new InstallerPreferencesStore(_root).ReadTheme());
    }

    [Fact]
    public void A_theme_outlives_the_run_that_chose_it()
    {
        new InstallerPreferencesStore(_root).WriteTheme(InstallerTheme.Dark);

        Assert.Equal(InstallerTheme.Dark, new InstallerPreferencesStore(_root).ReadTheme());
    }

    /// <summary>One preference must not overwrite the other on its way to disk.</summary>
    [Fact]
    public void Choosing_a_theme_leaves_the_language_alone()
    {
        var store = new InstallerPreferencesStore(_root);
        store.WriteLanguage(InstallerLanguage.Russian);

        store.WriteTheme(InstallerTheme.Light);

        var reopened = new InstallerPreferencesStore(_root);
        Assert.Equal(InstallerLanguage.Russian, reopened.ReadLanguage(new CultureInfo("en-US")));
        Assert.Equal(InstallerTheme.Light, reopened.ReadTheme());
    }

    /// <summary>
    /// The language shipped in a file of its own. Renaming the document must not quietly forget
    /// what somebody already chose — they would find the installer asking again in the wrong
    /// language, which is the failure #258 existed to fix.
    /// </summary>
    [Fact]
    public void A_language_chosen_before_the_rename_is_still_honoured()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "ui-language.json"),
            "{\"language\":\"Russian\"}");

        Assert.Equal(
            InstallerLanguage.Russian,
            new InstallerPreferencesStore(_root).ReadLanguage(new CultureInfo("en-US")));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"language\":\"klingon\",\"theme\":\"neon\"}")]
    [InlineData("{\"schemaVersion\":99,\"language\":\"English\",\"theme\":\"dark\"}")]
    public void An_unusable_file_falls_back_to_what_the_system_says(string content)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ui-preferences.json"), content);
        var store = new InstallerPreferencesStore(_root);

        Assert.Equal(InstallerLanguage.Russian, store.ReadLanguage(new CultureInfo("ru-RU")));
        Assert.Equal(InstallerTheme.System, store.ReadTheme());
    }

    /// <summary>
    /// Deliberately not the runtime root: that directory is validated against an exact list of
    /// names on every install, and a stray file in it once broke every installation after the
    /// download. The logs directory is already the installer's own.
    /// </summary>
    [Fact]
    public void Preferences_are_kept_beside_the_installer_logs_rather_than_in_the_runtime()
    {
        Assert.EndsWith(
            Path.Combine("LocalAi-installer-logs", "ui-preferences.json"),
            InstallerPreferencesStore.DefaultPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writing must not fail the run either. A read-only or missing directory means the choice
    /// is not remembered, which is a smaller loss than an installer that will not start.
    /// </summary>
    [Fact]
    public void A_choice_that_cannot_be_written_is_not_an_error()
    {
        var store = new InstallerPreferencesStore(Path.Combine(_root, "nested", "deeper"));

        store.WriteLanguage(InstallerLanguage.Russian);
        store.WriteTheme(InstallerTheme.Dark);

        Assert.Equal(InstallerLanguage.Russian, store.ReadLanguage(new CultureInfo("en-US")));
        Assert.Equal(InstallerTheme.Dark, store.ReadTheme());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
