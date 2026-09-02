using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// What the installer looks like, and which language it speaks, when nobody has said.
///
/// "System" is not a third colour scheme: it means the installer keeps following what Windows
/// says, including a change made while it is open. Somebody who has already told Windows which
/// they prefer has answered this question, and asking it again is the installer not listening.
/// </summary>
public enum InstallerTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Remembers the two answers that outlive a run, in one document.
///
/// The language arrived first (#258) in a file of its own; the theme (#259) is the same kind of
/// answer, so they share a document rather than accumulating a file each. A language chosen
/// before the rename is still read, because forgetting it would put somebody back in front of
/// an installer speaking the wrong language — the failure #258 existed to fix.
///
/// Kept beside the installer's own logs rather than in the runtime root: that directory is
/// validated against an exact list of names on every install, and a stray file in it once broke
/// every installation after the download.
///
/// Nothing here may fail a run. An unreadable file, a name no longer recognised, a schema from
/// a future version, a directory that cannot be created — each of them has a perfectly good
/// answer already, which is what the operating system says.
/// </summary>
public sealed class InstallerPreferencesStore(string directory)
{
    private const string FileName = "ui-preferences.json";

    /// <summary>The document this replaced, read once when the new one is not there yet.</summary>
    private const string FormerFileName = "ui-language.json";

    /// <summary>
    /// Written into every document and checked on the way back. A file from a future version is
    /// treated as unreadable rather than guessed at.
    /// </summary>
    private const int CurrentSchemaVersion = 1;

    private Document? loaded;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        RemovalMatrix.JournalDirectoryName,
        FileName);

    public static InstallerPreferencesStore Default =>
        new(Path.GetDirectoryName(DefaultPath)!);

    /// <summary>
    /// The remembered language, or the one the operating system implies. Any culture whose
    /// two-letter name is `ru` reads as Russian; everything else reads as English, because
    /// English is what the installer falls back to rather than a language it claims to speak.
    /// </summary>
    public InstallerLanguage ReadLanguage(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (Load().Language is { } remembered &&
            Enum.TryParse<InstallerLanguage>(remembered, ignoreCase: true, out var language))
        {
            return language;
        }

        return culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? InstallerLanguage.Russian
            : InstallerLanguage.English;
    }

    public InstallerTheme ReadTheme() =>
        Load().Theme is { } remembered &&
        Enum.TryParse<InstallerTheme>(remembered, ignoreCase: true, out var theme)
            ? theme
            : InstallerTheme.System;

    public void WriteLanguage(InstallerLanguage language) =>
        Save(Load() with { Language = language.ToString() });

    public void WriteTheme(InstallerTheme theme) =>
        Save(Load() with { Theme = theme.ToString() });

    private Document Load()
    {
        if (loaded is { } already)
        {
            return already;
        }

        loaded = ReadDocument() ?? ReadFormerDocument() ?? new Document(CurrentSchemaVersion, null, null);
        return loaded;
    }

    private Document? ReadDocument()
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { } document &&
                document.SchemaVersion <= CurrentSchemaVersion)
            {
                return document;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Falls through to the operating system's answer, which is always available.
        }

        return null;
    }

    private Document? ReadFormerDocument()
    {
        try
        {
            var path = Path.Combine(directory, FormerFileName);
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { } document)
            {
                // The former document had no schema version and no theme, so only the language
                // carries over. It is left on disk: deleting somebody's file to tidy up is not
                // this class's business.
                return new Document(CurrentSchemaVersion, document.Language, null);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return null;
    }

    private void Save(Document document)
    {
        // Held in memory as well as on disk: the run that made the choice must honour it even
        // when the disk refused, or a person would watch their own selection do nothing.
        loaded = document;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, FileName),
                JsonSerializer.Serialize(document));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not remembering is a smaller loss than an installer that will not start.
        }
    }

    private sealed record Document(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("theme")] string? Theme);
}
