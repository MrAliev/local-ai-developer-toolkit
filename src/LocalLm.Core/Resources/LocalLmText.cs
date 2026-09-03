using System.Globalization;
using System.Resources;

namespace LocalLm.Core.Resources;

/// <summary>
/// Everything the LocalLm tools say, in the language the reader's machine is set to.
///
/// Written by hand rather than generated. A generated accessor gives a property per key and
/// nothing else; these are format strings with holes whose order is part of the contract, and
/// naming the arguments at the one place that fills them is what stops the third argument of a
/// seven-hole sentence from quietly becoming the fourth.
///
/// Formatting is invariant on purpose, in every language. The numbers in these lines are quoted
/// verbatim by agents and parsed by tests, so `2.5` stays `2.5` on a machine that would write
/// `2,5` — only the words move. <see cref="LocalAi.Contracts.Localization.OutputCulture"/> says
/// why at greater length.
/// </summary>
public static class LocalLmText
{
    private static readonly ResourceManager Manager = new(
        "LocalLm.Core.Resources.LocalLmText",
        typeof(LocalLmText).Assembly);

    /// <summary>
    /// The raw string for a key, for the parity test and for callers that only need the words.
    /// A missing key returns its own name rather than null: a line reading "SavedNothing" is a
    /// bug report, and an empty line is not.
    /// </summary>
    public static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>
    /// The keys a language carries, for the test that refuses a half-translated language.
    ///
    /// The set is not disposed, and that is not an oversight: <see cref="ResourceManager"/>
    /// hands back the one it caches, so disposing it closes the resource for the whole process.
    /// Doing that here cost a suite in which every later lookup threw
    /// <see cref="ObjectDisposedException"/> — from a method whose only job was to count.
    /// </summary>
    public static IReadOnlyCollection<string> Keys(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var set = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        return set is null
            ? []
            : [.. set.Cast<System.Collections.DictionaryEntry>()
                .Select(entry => (string)entry.Key)];
    }

    /// <summary>The line every local tool returns, so a delegation is never silent.</summary>
    public static string Notice(
        string model,
        string residency,
        string detail,
        string duration,
        string saving) =>
        Format(nameof(Notice), model, residency, detail, duration, saving);

    /// <summary>The mark beside a model that did not fit in video memory.</summary>
    public static string ResidencyPartialOffload(int residentPercent) =>
        Format(nameof(ResidencyPartialOffload), residentPercent);

    public static string ResidencyCpu => Get(nameof(ResidencyCpu));

    /// <summary>Said once per process, not once per answer.</summary>
    public static string ResidencyAdvice => Get(nameof(ResidencyAdvice));

    /// <summary>How long it took, when the wait is worth naming apart from the work.</summary>
    public static string DurationWithQueue(string total, string queued) =>
        Format(nameof(DurationWithQueue), total, queued);

    public static string DurationSeconds(string seconds) =>
        Format(nameof(DurationSeconds), seconds);

    public static string ImagesRead(int count, string described) =>
        Format(nameof(ImagesRead), count, described);

    public static string FilesProcessed(int count, string names, string more) =>
        Format(nameof(FilesProcessed), count, names, more);

    public static string PromptOnly => Get(nameof(PromptOnly));

    public static string InputTruncated(int characterBudget) =>
        Format(nameof(InputTruncated), characterBudget);

    public static string FilesSkipped(int omitted) => Format(nameof(FilesSkipped), omitted);

    public static string LogTextRead(int characters, long fragments, int contextTokens) =>
        Format(nameof(LogTextRead), characters, fragments, contextTokens);

    public static string LogFileRead(
        string fileName,
        long kilobytes,
        long fragments,
        int contextTokens) =>
        Format(nameof(LogFileRead), fileName, kilobytes, fragments, contextTokens);

    public static string TranslationNotice(
        string model,
        string residency,
        string validation,
        string duration,
        string processedLocally,
        string generationSaved,
        string contextSaved) =>
        Format(
            nameof(TranslationNotice),
            model,
            residency,
            validation,
            duration,
            processedLocally,
            generationSaved,
            contextSaved);

    public static string SavingUnderHalfK => Get(nameof(SavingUnderHalfK));

    public static string SavedNothing => Get(nameof(SavedNothing));

    public static string SavedNegligible => Get(nameof(SavedNegligible));

    public static string SavedAbout(string band) => Format(nameof(SavedAbout), band);

    public static string TranslationFailed(string reason) =>
        Format(nameof(TranslationFailed), reason);

    public static string FileNotFound(string path) => Format(nameof(FileNotFound), path);

    public static string InvalidRequest(string reason) => Format(nameof(InvalidRequest), reason);

    public static string LocalModelFailed(string reason) =>
        Format(nameof(LocalModelFailed), reason);

    private static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), arguments);
}
