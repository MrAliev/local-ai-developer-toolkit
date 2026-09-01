using System.Text.RegularExpressions;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The start screen learned to ask which language somebody reads (#258); until the wizard behind
/// it answers in that language, the question is a courtesy that leads straight into eight pages
/// of English, with no way back to the screen where the choice was made.
///
/// These tests read the shell — the rail, the page headings, the buttons — because that is what
/// stays on screen the whole way through. A heading in one language over a body in the other is
/// the specific half-translation this is meant to prevent.
///
/// The headings are read through <see cref="WizardText"/> rather than by walking the wizard,
/// because two of the eight pages are only reachable by running an installation. Text nobody can
/// assert is text that quietly stays English.
/// </summary>
public sealed class WizardSpeaksRussianTests : IDisposable
{
    private readonly InstallerLanguage original = InstallerCulture.Current;

    public void Dispose() => InstallerCulture.Current = original;

    [Fact]
    public void The_rail_names_every_step_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        foreach (var step in new InstallerWizardViewModel().StepList)
        {
            AssertRussian(step.Title, "rail entry");
        }
    }

    /// <summary>
    /// The rail is built from a table that used to be a static field, which reads its strings
    /// once per process. Choosing Russian in a run that had already built it in English would
    /// then leave the rail behind — the one part of the window that never scrolls away.
    /// </summary>
    [Fact]
    public void The_rail_follows_a_language_chosen_after_it_was_first_read()
    {
        InstallerCulture.Current = InstallerLanguage.English;
        Assert.Equal("System check", new InstallerWizardViewModel().StepList[0].Title);

        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Equal("Проверка системы", new InstallerWizardViewModel().StepList[0].Title);
    }

    [Fact]
    public void Every_page_heading_and_body_is_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        foreach (var page in Enum.GetValues<InstallerPage>())
        {
            foreach (var isUpdate in new[] { false, true })
            {
                AssertRussian(WizardText.Title(page, isUpdate, hasRunError: false), $"{page} title");
                AssertRussian(WizardText.Title(page, isUpdate, hasRunError: true), $"{page} title");
                AssertRussian(
                    WizardText.Description(page, isUpdate, "Установить", hasRunError: false),
                    $"{page} description");
                AssertRussian(
                    WizardText.Description(page, isUpdate, "Установить", hasRunError: true),
                    $"{page} description");
            }
        }
    }

    [Fact]
    public void The_buttons_and_the_counter_are_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var wizard = new InstallerWizardViewModel();

        Assert.Equal("Установить", wizard.ActionText);
        Assert.Equal("Шаг 1 из 8", wizard.StepStatus);
        AssertRussian(wizard.CancelButtonText, "cancel button");
        AssertRussian(wizard.WindowTitle, "window title");
    }

    [Fact]
    public void An_update_run_says_update_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);

        Assert.Equal("Обновить", wizard.ActionText);
    }

    /// <summary>
    /// The confirm page tells the reader which button applies the run. It hard-coded the word
    /// "Install", which is already wrong on an update run in English; in Russian it also has to
    /// move, because the sentence puts the button at the end.
    /// </summary>
    [Fact]
    public void The_confirm_page_names_the_button_it_actually_has()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Contains(
            "«Обновить»",
            WizardText.Description(InstallerPage.Confirm, isUpdate: true, "Обновить", false),
            StringComparison.Ordinal);

        InstallerCulture.Current = InstallerLanguage.English;

        Assert.Contains(
            "click Update.",
            WizardText.Description(InstallerPage.Confirm, isUpdate: true, "Update", false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_english_wizard_is_left_exactly_as_it_was()
    {
        InstallerCulture.Current = InstallerLanguage.English;

        var wizard = new InstallerWizardViewModel();

        Assert.Equal("System check", wizard.StepList[0].Title);
        Assert.Equal(
            "How models run on this computer",
            WizardText.Title(InstallerPage.Models, isUpdate: false, hasRunError: false));
        Assert.Equal("Install", wizard.ActionText);
        Assert.Equal("Step 1 of 8", wizard.StepStatus);
    }

    private static void AssertRussian(string text, string what)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), $"{what} is empty");
        Assert.True(Regex.IsMatch(text, "[а-яА-ЯёЁ]"), $"{what} is still English: {text}");
    }
}
