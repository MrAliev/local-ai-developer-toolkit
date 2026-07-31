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

    [GeneratedRegex(
        @"^Ollama version (?<version>[0-9]{1,5}(?:\.[0-9]{1,5}){1,3}(?:[-+][0-9A-Za-z](?:[0-9A-Za-z.-]{0,30}[0-9A-Za-z])?)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedDisplayName();

    [GeneratedRegex(
        @"^[0-9]{1,5}(?:\.[0-9]{1,5}){1,3}(?:[-+][0-9A-Za-z](?:[0-9A-Za-z.-]{0,30}[0-9A-Za-z])?)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticLikeVersion();
}
