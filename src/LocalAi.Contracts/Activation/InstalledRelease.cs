using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts.Activation;

/// <summary>
/// Which published release the installed version directory came from.
///
/// The version pointer records a directory name — a commit id such as
/// <c>467ed5f0f9bf</c> — because that is what activation swaps and what the launcher
/// resolves. Nothing on a machine recorded the release version that directory was
/// published as, so every surface comparing "what is installed" against "what is newest"
/// was comparing a commit id with <c>0.1.51</c> and silently concluding there was nothing
/// to do (#255).
///
/// This document adds the missing half beside the pointer rather than inside it: the
/// pointer is written by the launcher under a compare-and-swap that hashes its exact bytes
/// and accepts exactly two properties, and a release version has no business making that
/// machinery more complicated.
///
/// It carries the directory it describes so a stale copy is detectable. A rollback performed
/// without a manifest moves the pointer and leaves this document behind; a reader that finds
/// the two disagreeing knows the answer is unknown rather than believing an old one.
/// </summary>
public sealed record InstalledRelease(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("versionDirectory")] string VersionDirectory,
    [property: JsonPropertyName("releaseVersion")] string ReleaseVersion)
{
    public const string FileName = "installed-release.json";

    public const int CurrentSchemaVersion = 1;
}

public sealed class InstalledReleaseStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _path;

    /// <param name="binRoot">
    /// The <c>bin</c> directory, beside <c>current.json</c> — the document describes the
    /// installation, not any one version, and a version directory is immutable once published.
    /// </param>
    public InstalledReleaseStore(string binRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        _path = Path.Combine(Path.GetFullPath(binRoot), InstalledRelease.FileName);
    }

    public string FilePath => _path;

    /// <summary>
    /// The release version of <paramref name="versionDirectory"/>, or null when this
    /// installation cannot say. Null is a real answer with three causes worth distinguishing
    /// nowhere: the document is missing because the installation predates it, it is
    /// unreadable, or it describes a different directory than the one now active.
    /// </summary>
    public string? ReadFor(string? versionDirectory)
    {
        if (string.IsNullOrWhiteSpace(versionDirectory))
        {
            return null;
        }

        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<InstalledRelease>(
                File.ReadAllText(_path),
                SerializerOptions);
            if (record is null ||
                record.SchemaVersion != InstalledRelease.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(record.ReleaseVersion) ||
                !string.Equals(
                    record.VersionDirectory,
                    versionDirectory,
                    StringComparison.Ordinal))
            {
                return null;
            }

            return record.ReleaseVersion;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the release a directory was published as. Replaced atomically, because every
    /// surface reads it and a half-written document would read as "unknown" — harmless, but
    /// the temp-then-move costs nothing.
    /// </summary>
    public void Write(string versionDirectory, string releaseVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseVersion);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(
                    new InstalledRelease(
                        InstalledRelease.CurrentSchemaVersion,
                        versionDirectory,
                        releaseVersion),
                    SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
