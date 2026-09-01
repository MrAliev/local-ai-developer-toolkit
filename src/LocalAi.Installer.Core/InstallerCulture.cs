namespace LocalAi.Installer.Core;

/// <summary>
/// The languages the installer speaks. The same two every document in this repository comes in.
/// </summary>
public enum InstallerLanguage
{
    English,
    Russian,
}

/// <summary>
/// Which language the installer is speaking, for the length of one run.
///
/// Process state rather than a parameter threaded through every call, because there is exactly
/// one installer window and it cannot be in two languages at once. It lives in the core rather
/// than beside the view models because the removal wizard's rows, presets and preview — the
/// sentences somebody actually consents to — are built here.
///
/// The one thing this deliberately does not touch is <see cref="System.Globalization"/>: numbers
/// and dates keep the operating system's formatting, which is what the rest of the machine uses.
/// </summary>
public static class InstallerCulture
{
    public static InstallerLanguage Current { get; set; } = InstallerLanguage.English;

    public static bool IsRussian => Current == InstallerLanguage.Russian;

    /// <summary>The one of two strings this language calls for.</summary>
    public static string Pick(string english, string russian) => IsRussian ? russian : english;

    public static string CurrentCultureCode =>
        IsRussian ? "ru" : System.Globalization.CultureInfo.InvariantCulture.TwoLetterISOLanguageName;
}
