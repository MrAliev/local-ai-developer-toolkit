using System.Collections;
using System.Globalization;
using System.Resources;

namespace LocalAi.Contracts.Localization;

/// <summary>
/// One assembly's strings, in the language the reader's machine is set to.
///
/// Each assembly that prints owns its own catalogue rather than everything sharing one: the
/// strings ship inside the assembly that uses them, so a project can be moved or dropped without
/// leaving orphaned entries behind in somebody else's resource file. What they share is this,
/// because the alternative was the same twenty lines of <see cref="ResourceManager"/> plumbing
/// copied per assembly, each free to drift on the two things that actually matter: which culture
/// the words come from, and which culture the numbers are formatted in.
///
/// Words follow the reader. Numbers do not: they are formatted invariantly in every language,
/// because these lines are quoted verbatim by agents and parsed by tests, and a decimal
/// separator that moves with the reader is a worse bargain than an English word.
/// See <see cref="OutputCulture.PinInvariantFormatting"/>.
/// </summary>
public sealed class TextCatalogue
{
    private readonly ResourceManager _manager;

    public TextCatalogue(string baseName, System.Reflection.Assembly assembly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentNullException.ThrowIfNull(assembly);
        _manager = new ResourceManager(baseName, assembly);
    }

    /// <summary>
    /// The string for a key. A key that is missing returns its own name rather than null: a line
    /// reading "SavedNothing" is a bug report somebody will act on, and a blank line is not.
    /// </summary>
    public string Get(string key) => _manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>The string with its holes filled, invariantly whatever the language.</summary>
    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), arguments);

    /// <summary>
    /// The keys one language carries, for the test that refuses a half-translated language.
    ///
    /// The set is deliberately not disposed: <see cref="ResourceManager"/> hands back the one it
    /// caches, so disposing it closes the resource for the whole process. Doing that cost a
    /// suite in which every later lookup threw <see cref="ObjectDisposedException"/> — from a
    /// method whose only job was to count.
    /// </summary>
    public IReadOnlyCollection<string> Keys(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var set = _manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        return set is null
            ? []
            : [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];
    }

    /// <summary>
    /// The keys this catalogue is missing in each translated language, empty when every language
    /// carries every string. Shared by the parity test of every assembly that has a catalogue,
    /// so a new one cannot ship with the check accidentally left out.
    /// </summary>
    public IReadOnlyList<string> Gaps()
    {
        var neutral = Keys(CultureInfo.InvariantCulture);
        var gaps = new List<string>();
        foreach (var language in OutputCulture.Supported
                     .Where(name => !name.Equals("en", StringComparison.Ordinal)))
        {
            var translated = Keys(CultureInfo.GetCultureInfo(language));
            gaps.AddRange(neutral
                .Except(translated, StringComparer.Ordinal)
                .Select(key => $"{language} is missing {key}"));
            gaps.AddRange(translated
                .Except(neutral, StringComparer.Ordinal)
                .Select(key => $"{language} has {key}, which the neutral resource does not"));
        }

        gaps.Sort(StringComparer.Ordinal);
        return gaps;
    }
}
