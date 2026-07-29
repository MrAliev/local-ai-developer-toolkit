using System.Text.RegularExpressions;

namespace LocalLm.Core;

public sealed record ProtectedTranslationSegment(string Token, string Original);

public sealed record TranslationPart(string Text, bool IsTranslatable);

public sealed record ProtectedTranslationText(
    string Text,
    IReadOnlyList<ProtectedTranslationSegment> Segments)
{
    public string Restore(string translated)
    {
        ArgumentNullException.ThrowIfNull(translated);
        var restored = translated;
        foreach (var segment in Segments)
        {
            var first = restored.IndexOf(segment.Token, StringComparison.Ordinal);
            var last = restored.LastIndexOf(segment.Token, StringComparison.Ordinal);
            if (first < 0 || first != last)
            {
                throw new InvalidDataException(
                    $"Translation did not preserve protected placeholder '{segment.Token}'.");
            }

            restored = restored.Replace(
                segment.Token,
                segment.Original,
                StringComparison.Ordinal);
        }

        return restored;
    }
}

public static partial class TranslationProtector
{
    public static IReadOnlyList<TranslationPart> SplitFencedCode(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var parts = new List<TranslationPart>();
        var offset = 0;
        foreach (Match match in FencedCode().Matches(source))
        {
            if (match.Index > offset)
            {
                parts.Add(new TranslationPart(
                    source.Substring(offset, match.Index - offset),
                    IsTranslatable: true));
            }

            parts.Add(new TranslationPart(match.Value, IsTranslatable: false));
            offset = match.Index + match.Length;
        }

        if (offset < source.Length)
        {
            parts.Add(new TranslationPart(source[offset..], IsTranslatable: true));
        }

        return Array.AsReadOnly(parts.ToArray());
    }

    public static ProtectedTranslationText ProtectFencedCode(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var segments = new List<ProtectedTranslationSegment>();
        var text = FencedCode().Replace(
            source,
            match =>
            {
                var token = $"__LOCALAI_PROTECTED_FENCE_{segments.Count:D4}__";
                segments.Add(new ProtectedTranslationSegment(token, match.Value));
                return token;
            });
        return new ProtectedTranslationText(
            text,
            Array.AsReadOnly(segments.ToArray()));
    }

    [GeneratedRegex(
        @"(?ms)^(`{3,}|~{3,})[^\r\n]*\r?\n.*?^\1[ \t]*(?:\r?\n|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex FencedCode();
}
