using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The start screen describes what each errand does, and one of those descriptions stopped
/// being true when the update path started folding the release page away: it promises "the
/// release you choose", and the wizard resolves it behind the first page without asking.
///
/// A screen that advertises a question the product deliberately stopped asking is worse than
/// one that says nothing — the person waits for it, and then wonders what they missed.
/// </summary>
[Collection(InstallerLanguageCollection.Name)]
public sealed class StartScreenTellsTheTruthTests
{
    // The language is process state now, so a class that asserts English says so. Run
    // after one that chose Russian, it would otherwise read that choice as its own — which
    // is exactly how this first failed.
    // xunit builds one instance per test, so this runs before each of them — a
    // static constructor runs once and lets whichever class went first decide.
    public StartScreenTellsTheTruthTests() => InstallerCulture.Current = InstallerLanguage.English;


    [Fact]
    public void The_update_errand_does_not_promise_a_choice_the_wizard_never_offers()
    {
        var update = Option(StartChoice.UpdateOrRepair);

        Assert.DoesNotContain("you choose", update.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And still says what it does keep, which is the reason somebody picks it over a clean
    /// reinstall.
    /// </summary>
    [Fact]
    public void The_update_errand_still_says_what_survives_it()
    {
        var update = Option(StartChoice.UpdateOrRepair);

        Assert.Contains("Indexes", update.Description, StringComparison.Ordinal);
        Assert.Contains("client integrations are kept", update.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every errand the screen offers has to describe itself. A row with an empty description is
    /// a button whose consequence the reader has to guess.
    /// </summary>
    [Fact]
    public void Every_errand_describes_itself()
    {
        foreach (var option in new InstallerStartViewModel().Actions)
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Title), $"{option.Choice} has no title");
            Assert.False(
                string.IsNullOrWhiteSpace(option.Description),
                $"{option.Choice} has no description");
        }
    }

    private static StartActionOption Option(StartChoice choice) =>
        new InstallerStartViewModel().Actions.Single(option => option.Choice == choice);
}
