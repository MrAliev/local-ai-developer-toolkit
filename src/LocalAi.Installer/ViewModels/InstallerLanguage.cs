using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The languages the installer speaks. The same two every document in this repository comes in.
/// </summary>
public enum InstallerLanguage
{
    English,
    Russian,
}

/// <summary>
/// Remembers which language somebody chose, so the answer outlives the run that gave it —
/// installing in Russian and then being asked again in English to uninstall is the installer
/// forgetting something it was told.
///
/// Kept beside the installer's own logs rather than in the runtime root: that directory is
/// validated against an exact list of names on every install, and a stray file in it once broke
/// every installation after the download.
///
/// Nothing here may fail a run. An unreadable file, a name no longer recognised, a directory
/// that cannot be created — each of them has a perfectly good answer already, which is what the
/// operating system says. Refusing to start over a preferences file would be absurd.
/// </summary>
public sealed class InstallerLanguageStore(string directory)
{
    private const string FileName = "ui-language.json";

    private InstallerLanguage? remembered;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi-installer-logs",
        FileName);

    public static InstallerLanguageStore Default =>
        new(Path.GetDirectoryName(DefaultPath)!);

    /// <summary>
    /// The remembered choice, or the one the operating system implies. Any culture whose
    /// two-letter name is `ru` reads as Russian; everything else reads as English, because
    /// English is what the installer falls back to rather than a language it claims to speak.
    /// </summary>
    public InstallerLanguage Read(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (remembered is { } choice)
        {
            return choice;
        }

        try
        {
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Preference>(File.ReadAllText(path)) is { } preference &&
                Enum.TryParse<InstallerLanguage>(preference.Language, ignoreCase: true, out var stored))
            {
                remembered = stored;
                return stored;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Falls through to the operating system's answer, which is always available.
        }

        return culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? InstallerLanguage.Russian
            : InstallerLanguage.English;
    }

    public void Write(InstallerLanguage language)
    {
        // Held in memory as well as on disk: the run that made the choice must honour it even
        // when the disk refused, or a person would watch their own selection do nothing.
        remembered = language;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, FileName),
                JsonSerializer.Serialize(new Preference(language.ToString())));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not remembering is a smaller loss than an installer that will not start.
        }
    }

    private sealed record Preference([property: JsonPropertyName("language")] string Language);
}
