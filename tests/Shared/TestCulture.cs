using System.Globalization;
using System.Runtime.CompilerServices;

namespace LocalAi.Tests.Shared;

/// <summary>
/// Puts a test assembly in the state a real process is put in by its entry point, and gives a
/// test a way to say it is about another language.
///
/// Two separate pins, because they answer two questions. Formatting is invariant in every
/// language: before this product could read its own machine it was built in
/// globalization-invariant mode, so every number it printed was invariant, and turning that mode
/// off to see the reader's language also turned <c>2.5 chunks/s</c> into <c>2,5 chunks/s</c> on
/// a Russian machine — which two tests caught. The words default to English so that a suite
/// asserting English text passes on a Russian developer's machine and in CI alike; a test about
/// the Russian half says so with <see cref="Reading"/> rather than depending on where it runs.
///
/// A module initializer rather than a fixture: it has to be true before the first test class is
/// constructed, and there is nothing to opt into or forget.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Pin()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    /// <summary>
    /// Reads the rest of the block in the named language, and puts the previous one back
    /// afterwards — including when the assertion inside fails, which is the case that would
    /// otherwise leave every later test in this assembly reading Russian.
    /// </summary>
    internal static IDisposable Reading(string language) => new Language(language);

    private sealed class Language : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

        internal Language(string language) =>
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);

        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}
