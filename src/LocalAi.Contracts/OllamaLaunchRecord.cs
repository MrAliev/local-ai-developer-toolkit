using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// The Ollama executable an installation established, so the broker can start it again later.
/// </summary>
/// <remarks>
/// It is written rather than looked up because looking one up at the moment of use is how a
/// background process ends up running whatever happens to answer to the name. The installer knows
/// which file it validated — against the uninstall entry, its approved directories and its
/// signed identity — and that is the only one worth starting unattended.
///
/// Ollama installs into a directory the user can write to, so the ACL check that guards the
/// winget executable cannot be reused here: it would reject an ordinary installation. Recording
/// the path the installer verified is what replaces it.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OllamaLaunchRecord(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string ExecutablePath,
    string? Version = null)
{
    public const string FileName = "ollama-launch.json";

    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Where that record lives, beside the other installation-wide documents in the runtime root so
/// the installer writes one copy and the broker reads it.
///
/// A missing, empty or malformed file reads as null. There is no default to fall back to: the
/// point of the record is that somebody verified this path, and a machine with no record is a
/// machine where nothing may be started.
/// </summary>
public sealed class OllamaLaunchRecordStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _path;

    public OllamaLaunchRecordStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _path = Path.Combine(runtimeRoot, OllamaLaunchRecord.FileName);
    }

    public OllamaLaunchRecord? Read()
    {
        try
        {
            var text = File.ReadAllText(_path);
            var record = JsonSerializer.Deserialize<OllamaLaunchRecord>(text, SerializerOptions);
            return record is null ||
                record.SchemaVersion != OllamaLaunchRecord.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(record.ExecutablePath) ||
                !Path.IsPathFullyQualified(record.ExecutablePath)
                ? null
                : record;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refuses a path that is not fully qualified, because a relative one would be resolved
    /// against whatever directory the broker happened to be started in.
    /// </summary>
    public void Save(string executablePath, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The recorded Ollama path must be fully qualified.",
                nameof(executablePath));
        }

        File.WriteAllText(
            _path,
            JsonSerializer.Serialize(
                new OllamaLaunchRecord(
                    OllamaLaunchRecord.CurrentSchemaVersion,
                    Path.GetFullPath(executablePath),
                    version),
                SerializerOptions));
    }
}
