using System.Globalization;
using System.Resources;
using LocalAi.Contracts;

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

    /// <summary>
    /// What a tool says when the request itself cannot be run. These reach the caller as the
    /// tool's answer rather than as a protocol error, so they are read by the same person as
    /// the notice line and belong in the same language.
    /// </summary>
    public static string ImageProfileUnsupported => Get(nameof(ImageProfileUnsupported));

    public static string NoImagePaths => Get(nameof(NoImagePaths));

    public static string TooManyImages(int given, int limit) =>
        Format(nameof(TooManyImages), given, limit);

    public static string NotAnImage(string path) => Format(nameof(NotAnImage), path);

    public static string ImageTooLarge(string path, long megabytes, long limit) =>
        Format(nameof(ImageTooLarge), path, megabytes, limit);

    public static string ImagesTooLargeTogether(long limitMegabytes) =>
        Format(nameof(ImagesTooLargeTogether), limitMegabytes);

    public static string ImagesTooManyPixels(long limit) =>
        Format(nameof(ImagesTooManyPixels), limit);

    public static string NotATextChatProfile(LocalTaskProfile profile) =>
        Format(nameof(NotATextChatProfile), profile);

    public static string TooManyFiles(int given, int limit) =>
        Format(nameof(TooManyFiles), given, limit);

    public static string TranslationNoReceipt => Get(nameof(TranslationNoReceipt));

    public static string TranslationValidationFailed(string detail) =>
        Format(nameof(TranslationValidationFailed), detail);

    /// <summary>What to tell somebody whose task has no model to run on.</summary>
    public static string NoModelAndCatalogNamesNone(LocalTaskProfile profile) =>
        Format(nameof(NoModelAndCatalogNamesNone), profile);

    public static string NoModelInstalledWithInstall(
        LocalTaskProfile profile,
        string models,
        string firstModel,
        string catalogVersion) =>
        Format(
            nameof(NoModelInstalledWithInstall),
            profile,
            models,
            firstModel,
            catalogVersion);

    public static string NoModelInstalled(LocalTaskProfile profile, string models) =>
        Format(nameof(NoModelInstalled), profile, models);

    public static string IneligibleWithModels(LocalTaskProfile profile, string models) =>
        Format(nameof(IneligibleWithModels), profile, models);

    public static string Ineligible(LocalTaskProfile profile) =>
        Format(nameof(Ineligible), profile);

    public static string QuestionDoesNotFitContext => Get(nameof(QuestionDoesNotFitContext));

    public static string LogTriageNoReceipt => Get(nameof(LogTriageNoReceipt));

    public static string CatalogMismatch(string client, string broker) =>
        Format(nameof(CatalogMismatch), client, broker);

    public static string ModelNotConfiguredForTriage(string model) =>
        Format(nameof(ModelNotConfiguredForTriage), model);

    public static string NoTriageModelNoneTried => Get(nameof(NoTriageModelNoneTried));

    public static string NoTriageModel(string attempted) =>
        Format(nameof(NoTriageModel), attempted);

    public static string ExactlyOneSource => Get(nameof(ExactlyOneSource));

    /// <summary>Not a failure: the answer triage_log gives for a log with nothing in it.</summary>
    public static string EmptyLog => Get(nameof(EmptyLog));

    public static string UnknownValue(string parameter, string value) =>
        Format(nameof(UnknownValue), parameter, value);

    /// <summary>
    /// What the translation validator found, lowercase because each one lands after a colon
    /// inside a sentence the notice line already started. The successful ones reach every
    /// translated answer, which is how a Russian notice line came to end in an English clause.
    /// </summary>
    public static string ValidatorEmptyTranslation => Get(nameof(ValidatorEmptyTranslation));

    public static string ValidatorUnexpectedFence => Get(nameof(ValidatorUnexpectedFence));

    public static string ValidatorPromptLeak => Get(nameof(ValidatorPromptLeak));

    public static string ValidatorExpanded(int from, int to) =>
        Format(nameof(ValidatorExpanded), from, to);

    public static string ValidatorPlausible => Get(nameof(ValidatorPlausible));

    public static string ValidatorStructurePreserved => Get(nameof(ValidatorStructurePreserved));

    public static string ValidatorCountMismatch(string what, int expected, int actual) =>
        Format(nameof(ValidatorCountMismatch), what, expected, actual);

    public static string ValidatorProtectedTokensChanged(string what) =>
        Format(nameof(ValidatorProtectedTokensChanged), what);

    public static string ValidatorHeadings => Get(nameof(ValidatorHeadings));

    public static string ValidatorFenceMarkers => Get(nameof(ValidatorFenceMarkers));

    public static string ValidatorListMarkers => Get(nameof(ValidatorListMarkers));

    public static string ValidatorFencedCode => Get(nameof(ValidatorFencedCode));

    public static string ValidatorInlineCode => Get(nameof(ValidatorInlineCode));

    public static string ValidatorUrls => Get(nameof(ValidatorUrls));

    public static string ValidatorPlaceholders => Get(nameof(ValidatorPlaceholders));

    private static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), arguments);
}
