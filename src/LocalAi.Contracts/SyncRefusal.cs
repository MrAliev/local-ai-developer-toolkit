namespace LocalAi.Contracts;

/// <summary>
/// The shape of the line a bounded sync prints when it declines, named once so the writer and
/// the reader cannot drift apart.
///
/// They are in different processes: `localai sync` prints it, the `index_refresh` MCP tool
/// parses it out of stdout. Spelled out on both sides, a rename on one would leave the other
/// silently reading every refusal as an ordinary result — which is the failure #275 is about,
/// returning by a different door.
/// </summary>
public static class SyncRefusal
{
    public const string Prefix = "REFUSED ";

    public const string FilesKey = "files=";

    public const string LimitFlag = "--max-inline-files";

    public static string Line(string repositoryId, int files, int limit) =>
        $"{Prefix}repository={repositoryId} {FilesKey}{files} limit={limit} overlays=0";

    /// <summary>The declined file count, or null when this is not a refusal line.</summary>
    public static int? Files(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var line = output
            .Split('\n')
            .Select(item => item.Trim())
            .FirstOrDefault(item => item.StartsWith(Prefix, StringComparison.Ordinal));
        var token = line
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(FilesKey, StringComparison.Ordinal));
        return token is not null && int.TryParse(token[FilesKey.Length..], out var files)
            ? files
            : null;
    }
}
