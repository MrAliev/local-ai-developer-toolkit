using System.Text.RegularExpressions;

namespace LocalAi.ReleaseSigner;

/// <summary>
/// A release version, and the one question worth asking about it: is it ahead of everything
/// already published?
///
/// Every release so far has been a number typed four times — into two note filenames, into the
/// publish script and into the tag — and nothing compared it to what already exists. Typing one
/// that is already taken does not fail until the very last step, after a full self-contained
/// build has been produced and signed against it.
/// </summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$")]
    private static partial Regex Pattern { get; }

    public static ReleaseVersion Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var match = Pattern.Match(value);
        if (!match.Success)
        {
            throw new ArgumentException(
                $"'{value}' is not a release version. Expected 1.2.3 or 1.2.3-rc.1.",
                nameof(value));
        }

        return new ReleaseVersion(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value),
            match.Groups[4].Success ? match.Groups[4].Value : null);
    }

    public static bool TryParse(string? value, out ReleaseVersion? version)
    {
        version = null;
        if (value is null || !Pattern.IsMatch(value))
        {
            return false;
        }

        version = Parse(value);
        return true;
    }

    /// <summary>
    /// Ordering follows semantic versioning in the one respect that matters here: a pre-release
    /// sorts before the release it leads to, so 0.1.36-rc.1 does not count as newer than 0.1.36.
    /// Pre-release identifiers are compared as text, which is enough for the rc.N this project
    /// has ever used and honest about being no more than that.
    /// </summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var number = Major.CompareTo(other.Major);
        if (number != 0)
        {
            return number;
        }

        number = Minor.CompareTo(other.Minor);
        if (number != 0)
        {
            return number;
        }

        number = Patch.CompareTo(other.Patch);
        if (number != 0)
        {
            return number;
        }

        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (left, right) => string.CompareOrdinal(left, right),
        };
    }

    public override string ToString() =>
        PreRelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    /// <summary>
    /// The newest version among <paramref name="tags"/>, ignoring anything that is not a release
    /// version. The repository carries a handful of early <c>v0.1.x</c> tags that the current
    /// scheme dropped; they are not release versions under this format and are not silently
    /// reinterpreted as if they were.
    /// </summary>
    public static ReleaseVersion? Newest(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ReleaseVersion? newest = null;
        foreach (var tag in tags)
        {
            if (TryParse(tag.Trim(), out var candidate) && candidate!.CompareTo(newest) > 0)
            {
                newest = candidate;
            }
        }

        return newest;
    }
}
