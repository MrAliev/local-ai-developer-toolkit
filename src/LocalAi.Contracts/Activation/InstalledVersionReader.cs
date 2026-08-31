using System.Text.Json;

namespace LocalAi.Contracts.Activation;

/// <summary>
/// What is installed, in the two forms a caller may need: the directory the pointer names,
/// and — when this installation recorded it — the release it was published as.
/// </summary>
/// <param name="VersionDirectory">
/// The directory name from <c>current.json</c>, or null when there is no installation.
/// </param>
/// <param name="ReleaseVersion">
/// The release that directory came from, or null when this installation cannot say: it
/// predates the record, the record is unreadable, or it describes a different directory.
/// </param>
public sealed record InstalledVersion(string? VersionDirectory, string? ReleaseVersion)
{
    public static InstalledVersion None { get; } = new(null, null);

    public bool Exists => VersionDirectory is not null;

    /// <summary>
    /// What to show a person. The release version when it is known, the directory otherwise —
    /// a commit id is a poor name for a version, but it is the true one, and inventing a
    /// prettier answer is how #255 happened.
    /// </summary>
    public string? DisplayName => ReleaseVersion ?? VersionDirectory;
}

/// <summary>
/// Reads the installed version from the runtime root, for the surfaces that report it:
/// <c>localai doctor</c>, <c>localai update</c> and the update notice on <c>index_status</c>.
///
/// One reader, because three copies of "parse current.json" is how the three of them managed
/// to compare a commit id against a release version in three places at once.
/// </summary>
public static class InstalledVersionReader
{
    public static InstalledVersion Read(string runtimeRoot)
    {
        if (string.IsNullOrWhiteSpace(runtimeRoot))
        {
            return InstalledVersion.None;
        }

        var binRoot = Path.Combine(runtimeRoot, "bin");
        var directory = ReadPointerVersion(Path.Combine(binRoot, "current.json"));
        return directory is null
            ? InstalledVersion.None
            : new(directory, new InstalledReleaseStore(binRoot).ReadFor(directory));
    }

    private static string? ReadPointerVersion(string pointerPath)
    {
        try
        {
            if (!File.Exists(pointerPath))
            {
                return null;
            }

            // As text rather than as bytes, so a pointer carrying a byte order mark reads the
            // same as one without: every other reader of this file already tolerates it.
            using var document = JsonDocument.Parse(File.ReadAllText(pointerPath));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
