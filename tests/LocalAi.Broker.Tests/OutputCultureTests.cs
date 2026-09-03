using System.Globalization;
using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Which language the product answers in, decided once and the same way everywhere.
///
/// The installer already worked this out for itself: a remembered choice, and the operating
/// system's answer when there is none. Nothing outside the installer had any rule at all, so the
/// language of a string was whatever the string beside it happened to be — which is how one
/// method came to emit an English truncation marker and a Russian truncation note ten lines
/// apart.
///
/// This is the same rule, in the one assembly everything else references.
/// </summary>
public sealed class OutputCultureTests
{
    [Fact]
    public void A_reader_whose_system_is_Russian_is_answered_in_Russian()
    {
        var resolved = OutputCulture.Resolve(requested: null, new CultureInfo("ru-RU"));

        Assert.Equal("ru", resolved.TwoLetterISOLanguageName);
    }

    /// <summary>
    /// English is not one language among several here: it is what the product falls back to
    /// rather than a language it claims to speak. A locale with no translation gets it, and
    /// the resource fallback does that on its own — so this only has to not get in the way.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void Every_other_reader_is_answered_in_English(string system)
    {
        var resolved = OutputCulture.Resolve(requested: null, new CultureInfo(system));

        Assert.NotEqual("ru", resolved.TwoLetterISOLanguageName);
    }

    /// <summary>
    /// The manual switch. Somebody working in English on a Russian machine has no other way to
    /// say so, and the operating system's answer is a default rather than a verdict.
    /// </summary>
    [Fact]
    public void An_explicit_choice_outranks_the_operating_system()
    {
        Assert.Equal(
            "en",
            OutputCulture.Resolve("en", new CultureInfo("ru-RU")).TwoLetterISOLanguageName);
        Assert.Equal(
            "ru",
            OutputCulture.Resolve("ru", new CultureInfo("en-US")).TwoLetterISOLanguageName);
    }

    /// <summary>
    /// A preference file, an environment variable and a command-line flag are all things a
    /// person can typo, and none of them is worth refusing to run over. The operating system's
    /// answer is always available and always sane.
    /// </summary>
    [Theory]
    [InlineData("klingon")]
    [InlineData("  ")]
    [InlineData("../../etc")]
    public void An_unusable_choice_falls_back_to_the_system_rather_than_failing(string requested)
    {
        var resolved = OutputCulture.Resolve(requested, new CultureInfo("ru-RU"));

        Assert.Equal("ru", resolved.TwoLetterISOLanguageName);
    }

    /// <summary>
    /// Applying it is a separate step from deciding it, because the entry points are the only
    /// place that may change process state, and a test that resolves must not change the
    /// language of the test run around it.
    /// </summary>
    [Fact]
    public void Applying_the_choice_sets_the_language_new_threads_start_in()
    {
        var before = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            OutputCulture.Apply("ru", new CultureInfo("en-US"));

            Assert.Equal("ru", CultureInfo.DefaultThreadCurrentUICulture?.TwoLetterISOLanguageName);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = before;
        }
    }

    /// <summary>
    /// Every language this installation claims to speak has its own answer from
    /// <see cref="OutputCulture.Pick"/>.
    ///
    /// The pair it picks from is the update-check disclosure, which is consent and therefore
    /// lives as two constants in one file rather than as a catalogue key — a parity test can
    /// prove a key exists in both languages, never that two paragraphs still make the same
    /// promises. The cost of that is this: <c>Pick</c> names its languages, and the procedure
    /// <see cref="OutputCulture.Supported"/> documents — add a resource file, add a name here —
    /// would otherwise leave a third language reading every line in its own words and the one
    /// paragraph that asks permission in English, with nothing failing.
    /// </summary>
    [Fact]
    public void Pick_has_an_answer_for_every_language_this_installation_speaks()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var language in OutputCulture.Supported)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                var picked = OutputCulture.Pick("english", "russian");

                Assert.True(
                    language.Equals("en", StringComparison.Ordinal)
                        ? picked == "english"
                        : picked != "english",
                    $"Pick answers '{picked}' to a reader of {language}, which is the English " +
                    "text. Every supported language needs its own branch here, or the " +
                    "disclosure it picks belongs in a catalogue after all.");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
