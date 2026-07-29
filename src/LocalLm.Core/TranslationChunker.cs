namespace LocalLm.Core;

public sealed record TranslationChunk(int Index, string Text);

public static class TranslationChunker
{
    public const int DefaultMaxInputCharacters = 48_000;

    public static IReadOnlyList<TranslationChunk> Chunk(
        string source,
        int maxCharacters = DefaultMaxInputCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 100);
        var chunks = new List<TranslationChunk>();
        var offset = 0;
        while (offset < source.Length)
        {
            var remaining = source.Length - offset;
            var length = Math.Min(maxCharacters, remaining);
            if (length < remaining)
            {
                var window = source.AsSpan(offset, length);
                var minimumBreak = length / 2;
                var breakAt = LastBreak(window, "\r\n\r\n", minimumBreak);
                breakAt = Math.Max(breakAt, LastBreak(window, "\n\n", minimumBreak));
                breakAt = Math.Max(breakAt, LastBreak(window, "\r\n", minimumBreak));
                breakAt = Math.Max(breakAt, LastBreak(window, "\n", minimumBreak));
                breakAt = Math.Max(breakAt, LastBreak(window, " ", minimumBreak));
                if (breakAt > 0)
                {
                    length = breakAt;
                }
            }

            chunks.Add(new TranslationChunk(
                chunks.Count,
                source.Substring(offset, length)));
            offset += length;
        }

        return chunks.AsReadOnly();
    }

    private static int LastBreak(
        ReadOnlySpan<char> value,
        string separator,
        int minimum)
    {
        var index = value.LastIndexOf(separator, StringComparison.Ordinal);
        return index < minimum ? -1 : index + separator.Length;
    }
}
