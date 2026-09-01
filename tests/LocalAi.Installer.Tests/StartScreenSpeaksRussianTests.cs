using LocalAi.Contracts.Activation;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Every document in this repository comes in both languages; the installer spoke only English
/// (#258). The choice belongs on the start screen because that is the first thing anybody sees,
/// and it has to take effect there rather than from the next window — telling the installer
/// which language you read and having it answer in the other one is the failure being fixed.
/// </summary>
public sealed class StartScreenSpeaksRussianTests : IDisposable
{
    private readonly RemovalFixture machine = new();
    private readonly InstallerLanguage original = InstallerCulture.Current;

    public void Dispose()
    {
        InstallerCulture.Current = original;
        machine.Dispose();
    }

    [Fact]
    public void The_headline_names_the_release_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Equal(
            "LocalAi 0.1.51 установлен на этом компьютере.",
            Start("0.1.51").Headline);
    }

    /// <summary>The product name is never transliterated and never declined.</summary>
    [Fact]
    public void The_product_keeps_its_name_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.DoesNotContain("ЛокалАй", Start("0.1.51").Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_release_reads_the_same_way_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var start = Start(release: null);

        Assert.Equal("LocalAi установлен на этом компьютере.", start.Headline);
        Assert.StartsWith("Сборка 467ed5f0f9bf.", start.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_errand_speaks_russian_too()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var start = Start("0.1.51");

        Assert.Equal("Установить LocalAi", start.Option(StartChoice.Install).Title);
        Assert.Equal("Обновить или восстановить", start.Option(StartChoice.UpdateOrRepair).Title);
        Assert.Equal("Чистая переустановка", start.Option(StartChoice.CleanReinstall).Title);
        Assert.Equal("Удалить LocalAi", start.Option(StartChoice.Remove).Title);
    }

    /// <summary>
    /// The wizard that follows is still English. Saying so on the screen where the choice is
    /// made is honest; letting somebody discover it on the next window, from which there is no
    /// way back, is not.
    /// </summary>
    [Fact]
    public void The_russian_screen_admits_the_wizard_is_not_translated_yet()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Contains("на английском", Start("0.1.51").TranslationNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_english_screen_says_nothing_of_the_kind()
    {
        InstallerCulture.Current = InstallerLanguage.English;

        Assert.Empty(Start("0.1.51").TranslationNotice);
    }

    /// <summary>
    /// Choosing a language repaints the screen. Anything else means the first thing the
    /// installer does after being told which language you read is to keep using the other.
    /// </summary>
    [Fact]
    public void Choosing_a_language_changes_what_is_already_on_screen()
    {
        InstallerCulture.Current = InstallerLanguage.English;
        var start = Start("0.1.51");
        var announced = new List<string>();
        start.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        start.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Contains(nameof(InstallerStartViewModel.Headline), announced);
        Assert.Contains("установлен на этом компьютере.", start.Headline, StringComparison.Ordinal);
        Assert.Equal("Установить LocalAi", start.Option(StartChoice.Install).Title);
    }

    private InstallerStartViewModel Start(string? release) =>
        new(
            machine.LocalAppData,
            readInstalledVersion: () => new InstalledVersion("467ed5f0f9bf", release));
}
