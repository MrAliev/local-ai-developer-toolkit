using System.Text.RegularExpressions;

namespace LocalAi.Installer.Core.Diagnosis;

internal static partial class OllamaVersionPolicy
{
    private const int MaximumVersionLength = 64;

    public static bool TryValidateDisplayName(
        string? displayName,
        out string? displayNameVersion)
    {
        displayNameVersion = null;
        if (string.Equals(
                displayName,
                "Ollama",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (displayName is null || displayName.Length > 96)
        {
            return false;
        }

        var match = VersionedDisplayName().Match(displayName);
        if (!match.Success)
        {
            return false;
        }

        displayNameVersion = Validate(
            match.Groups["version"].Value);
        return displayNameVersion is not null;
    }

    public static string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumVersionLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !SemanticLikeVersion().IsMatch(value))
        {
            return null;
        }

        return value;
    }

    public static bool TryResolveConsistentVersion(
        string? displayVersion,
        string? fileVersion,
        string? displayNameVersion,
        out string? detectedVersion)
    {
        detectedVersion = null;
        var validVersions = new[]
        {
            Validate(displayVersion),
            Validate(fileVersion),
            Validate(displayNameVersion),
        }.Where(version => version is not null).Cast<string>().ToArray();
        if (validVersions.Length == 0)
        {
            return false;
        }

        var expected = NormalizeForComparison(validVersions[0]);
        if (validVersions.Skip(1).Any(
                version => !string.Equals(
                    expected,
                    NormalizeForComparison(version),
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        detectedVersion = validVersions[0];
        return true;
    }

    private static string NormalizeForComparison(string version)
    {
        var suffixIndex = version.IndexOfAny(['-', '+']);
        var core = suffixIndex < 0 ? version : version[..suffixIndex];
        var suffix = suffixIndex < 0 ? string.Empty : version[suffixIndex..];
        var components = core
            .Split('.')
            .Select(component => int.Parse(
                component,
                System.Globalization.CultureInfo.InvariantCulture))
            .Concat(Enumerable.Repeat(0, 4))
            .Take(4);
        return $"{string.Join('.', components)}{suffix}";
    }

    [GeneratedRegex(
        @"^Ollama version (?<version>[0-9]{1,5}(?:\.[0-9]{1,5}){1,3}(?:[-+][0-9A-Za-z](?:[0-9A-Za-z.-]{0,30}[0-9A-Za-z])?)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedDisplayName();

    [GeneratedRegex(
        @"^[0-9]{1,5}(?:\.[0-9]{1,5}){1,3}(?:[-+][0-9A-Za-z](?:[0-9A-Za-z.-]{0,30}[0-9A-Za-z])?)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticLikeVersion();
}
