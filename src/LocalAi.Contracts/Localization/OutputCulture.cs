using System.Globalization;

namespace LocalAi.Contracts.Localization;

/// <summary>
/// Which language the product answers in, for the length of one process.
///
/// The installer had already answered this question for itself — a remembered choice, and the
/// operating system's answer when there is none — and nothing outside the installer had answered
/// it at all. The language of a string was therefore whatever the string next to it happened to
/// be, which is how one method came to emit an English truncation marker and a Russian
/// truncation note ten lines apart, and how the block installed into every user's agent
/// configuration came to spend a paragraph teaching an agent to read Russian output.
///
/// The decision and the applying of it are separate on purpose. Resolving is a pure function
/// anything may call; applying changes process state, which only an entry point may do.
/// </summary>
public static class OutputCulture
{
    /// <summary>
    /// The manual switch, for a reader whose machine does not speak the language they work in.
    /// An environment variable rather than a settings file because the servers this governs are
    /// started by a launcher and read no configuration of their own before their first line of
    /// output.
    /// </summary>
    public const string EnvironmentVariable = "LOCALAI_LANGUAGE";

    /// <summary>
    /// The languages there are resources for. Adding one is adding a <c>.resx</c> beside each
    /// neutral one and a name here, and the parity test refuses a language that only half
    /// exists.
    ///
    /// English is not one entry among several: it is the neutral resource every other language
    /// falls back to, so a locale absent from this list is answered in English rather than
    /// refused.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["en", "ru"];

    /// <summary>The language the product answers in when it has no reason to think otherwise.</summary>
    public static CultureInfo Fallback => CultureInfo.GetCultureInfo("en");

    /// <summary>
    /// The language to answer in: the explicit choice when there is a usable one, otherwise the
    /// operating system's, otherwise English.
    ///
    /// Nothing here may throw. A preference file, an environment variable and a command-line
    /// flag are all things a person can typo, and none of them is worth refusing to start over
    /// when there is a perfectly good answer already — the one the operating system gives.
    /// </summary>
    public static CultureInfo Resolve(string? requested, CultureInfo operatingSystem)
    {
        ArgumentNullException.ThrowIfNull(operatingSystem);
        return Match(requested) ?? Match(operatingSystem.TwoLetterISOLanguageName) ?? Fallback;

        static CultureInfo? Match(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            // A regional name answers to its language: ru-RU, ru-KZ and ru all read the same
            // resources, and a translation per region is not a thing this product has.
            var wanted = language.Trim();
            var known = Supported.FirstOrDefault(supported =>
                wanted.Equals(supported, StringComparison.OrdinalIgnoreCase) ||
                wanted.StartsWith(supported + "-", StringComparison.OrdinalIgnoreCase));

            return known is null ? null : CultureInfo.GetCultureInfo(known);
        }
    }

    /// <summary>
    /// Makes the resolved language the one resources are read in, for this thread and every
    /// thread started after it.
    ///
    /// The language only. Formatting is a separate decision and belongs to
    /// <see cref="PinInvariantFormatting"/>, so that resolving a language in a test does not
    /// silently change how the test run prints numbers.
    /// </summary>
    public static void Apply(string? requested, CultureInfo operatingSystem)
    {
        var resolved = Resolve(requested, operatingSystem);
        CultureInfo.DefaultThreadCurrentUICulture = resolved;
        CultureInfo.CurrentUICulture = resolved;
    }

    /// <summary>
    /// Numbers, durations and dates stay invariant whatever language the words are in.
    ///
    /// This is not a detail. Until this product could read its own machine it was built with
    /// globalization-invariant mode on, so every number it ever printed was invariant; turning
    /// that mode off to see the language would have turned `2.5 chunks/s` into `2,5 chunks/s`
    /// on a Russian machine, which is a change nobody asked for and which two tests caught. The
    /// notice line is also quoted verbatim by agents and parsed by people, and a decimal
    /// separator that moves with the reader is a worse bargain than an English word.
    ///
    /// The installer deliberately does the opposite — its numbers and dates follow the
    /// operating system, because a person is reading them in a window rather than relaying them
    /// — so it does not call this.
    /// </summary>
    public static void PinInvariantFormatting()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    /// <summary>
    /// The entry-point form, in the order a reader would expect to be obeyed: what this run was
    /// told, then what this installation was told, then the machine.
    ///
    /// The environment variable outranks the stored choice because it is the narrower statement
    /// — it is about this process, and somebody who set it is standing right there.
    /// </summary>
    public static void Apply()
    {
        Apply(
            Environment.GetEnvironmentVariable(EnvironmentVariable) ?? Stored(),
            CultureInfo.CurrentUICulture);
        PinInvariantFormatting();
    }

    /// <summary>
    /// One of two texts, by the language this process resolved to.
    ///
    /// For the paragraph that cannot live in a catalogue: the update-check disclosure is
    /// consent, and consent has to make the same promises in every language it is asked in.
    /// A parity test proves a key exists in both files; nothing can prove two paragraphs
    /// still say the same four things, so they are kept as a pair in one file and chosen
    /// here.
    ///
    /// Deliberately not the installer's <c>InstallerCulture.Pick</c>: that is a settable
    /// static which only the installer window ever sets, so a CLI asking it would be told
    /// English on every machine and would look like it was working.
    /// </summary>
    public static string Pick(string english, string russian) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("ru", StringComparison.OrdinalIgnoreCase) ? russian : english;

    private static string? Stored()
    {
        try
        {
            return OutputLanguageStore.Default.Read();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Locating the runtime root can fail on a machine with no installation at all; the
            // operating system's answer needs neither.
            return null;
        }
    }
}
