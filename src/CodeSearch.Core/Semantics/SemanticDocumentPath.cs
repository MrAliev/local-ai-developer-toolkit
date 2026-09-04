namespace CodeSearch.Core.Semantics;

/// <summary>
/// The one rule for what a document path may be on the way in: relative to the repository, the
/// way search_code prints it, with no rooted, empty, <c>.</c> or <c>..</c> segment.
///
/// It is asked twice, and it has to be the same question both times. The tool and console edges
/// ask it first and refuse by name, in the reader's language; the navigation service asks it
/// again and throws, for a caller that came in some other way. Two spellings of the rule would
/// let a path through one edge that the other refuses.
/// </summary>
public static class SemanticDocumentPath
{
    public static bool IsRepositoryRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        return !Path.IsPathRooted(normalized) &&
               !normalized.Split('/').Any(segment => segment is "" or "." or "..");
    }
}
