using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts.Localization;

/// <summary>
/// The language somebody chose, for the processes that are never in a position to ask.
///
/// The installer asks on screen and remembers the answer beside its own logs. The CLI and the
/// two MCP servers are started by a launcher and print before anybody could answer anything, so
/// their answer has to already be on disk — in the settings directory, beside the residency and
/// update-check policies, which is where an installation-wide setting belongs.
///
/// Absent means "follow this machine", not "English". A missing file is the ordinary state of a
/// working installation. Nothing here may fail a run either: a preferences file that cannot be
/// read is a reason to fall back on the operating system's answer, never a reason to refuse to
/// print.
/// </summary>
public sealed class OutputLanguageStore
{
    public const string FileName = "ui-language.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _runtimeRoot;

    public OutputLanguageStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
    }

    /// <summary>Where the choice is read from, which may still be the pre-split loose file.</summary>
    public static string PathFor(string runtimeRoot) =>
        RuntimeDirectories.SettingsFile(runtimeRoot, FileName);

    public static OutputLanguageStore Default =>
        new(ModelResidencyPolicyStore.DefaultRuntimeRoot);

    /// <summary>
    /// The chosen language, or null when this installation follows the machine it runs on.
    /// A language that is no longer one of <see cref="OutputCulture.Supported"/> reads as no
    /// choice: dropping a translation must not leave installations pointing at it.
    /// </summary>
    public string? Read()
    {
        try
        {
            var path = PathFor(_runtimeRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            var preference = JsonSerializer.Deserialize<Preference>(File.ReadAllBytes(path));
            var language = preference?.Language;
            return string.IsNullOrWhiteSpace(language) ||
                !OutputCulture.Supported.Contains(language, StringComparer.OrdinalIgnoreCase)
                ? null
                : language;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores a choice, or clears it when given null. Clearing removes the file rather than
    /// writing an empty one, so "follows the machine" has exactly one representation on disk.
    /// </summary>
    public void Write(string? language)
    {
        var path = RuntimeDirectories.SettingsFileForWriting(_runtimeRoot, FileName);
        if (string.IsNullOrWhiteSpace(language))
        {
            Delete(path);
            RuntimeDirectories.DiscardLegacySettingsFile(_runtimeRoot, FileName);
            return;
        }

        if (!OutputCulture.Supported.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(
                nameof(language),
                language,
                "The product has no resources for that language. Supported: " +
                string.Join(", ", OutputCulture.Supported) + ".");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new Preference(language.ToLowerInvariant()),
                SerializerOptions));
        RuntimeDirectories.DiscardLegacySettingsFile(_runtimeRoot, FileName);
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Not forgetting is a smaller loss than a command that fails while tidying up.
        }
    }

    private sealed record Preference([property: JsonPropertyName("language")] string Language);
}
