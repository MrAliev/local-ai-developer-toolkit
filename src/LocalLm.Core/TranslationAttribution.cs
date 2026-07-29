using System.Text.RegularExpressions;

namespace LocalLm.Core;

public static partial class TranslationAttribution
{
    public static string Append(
        string translated,
        string targetLanguage,
        string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translated);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var withoutExisting = ExistingAttribution()
            .Replace(translated.TrimEnd(), string.Empty)
            .TrimEnd();
        var note = targetLanguage.Contains("Russian", StringComparison.OrdinalIgnoreCase) ||
                   targetLanguage.Contains("рус", StringComparison.OrdinalIgnoreCase)
            ? $"Перевод выполнен локальной моделью: {model}."
            : $"Translation performed by the local model: {model}.";
        return $"{withoutExisting}\r\n\r\n{note}\r\n";
    }

    [GeneratedRegex(
        @"(?:\r?\n){0,2}(?:Translation performed by the local model|Перевод выполнен локальной моделью):\s*[^\r\n]+\.\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExistingAttribution();
}
