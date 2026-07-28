using System.Text;

namespace CodeSearch.Core.Chunking;

/// <summary>
/// Fallback chunker for everything that isn't C#: a sliding window over lines with overlap.
/// No syntax awareness, but it keeps exact line numbers, which is what makes a hit actionable.
/// </summary>
public sealed class GenericTextChunker : IChunker
{
    private const int WindowLines = 60;
    private const int OverlapLines = 12;

    public IEnumerable<Chunk> Split(string relPath, string content)
    {
        // A NUL byte means we were handed something binary that happens to carry a text
        // extension. Embedding it produces a meaningless vector, so drop the file.
        if (content.IndexOf('\0') >= 0)
        {
            yield break;
        }

        var lines = SourceLines.Split(content);
        if (lines.Length == 0)
        {
            yield break;
        }

        var fileName = Path.GetFileName(relPath);
        var step = WindowLines - OverlapLines;
        var part = 0;
        var total = Math.Max(1, (int)Math.Ceiling((double)lines.Length / step));

        for (var start = 0; start < lines.Length; start += step)
        {
            var end = Math.Min(start + WindowLines, lines.Length);
            part++;

            var body = string.Join("\n", lines[start..end]);
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            if (body.Length > ChunkLimits.MaxChars)
            {
                body = body[..ChunkLimits.MaxChars];
            }

            var text = new StringBuilder()
                .Append("File: ").AppendLine(relPath)
                .Append("Lines: ").Append(start + 1).Append('-').Append(end).AppendLine()
                .AppendLine()
                .Append(body)
                .ToString();

            yield return new Chunk
            {
                RelPath = relPath,
                Kind = ChunkKind.Text,
                Symbol = total > 1 ? $"{fileName} [{part}/{total}]" : fileName,
                Signature = $"{relPath}:{start + 1}-{end}",
                Namespace = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? string.Empty,
                StartLine = start + 1,
                EndLine = end,
                EmbedText = text,
            };

            if (end == lines.Length)
            {
                break;
            }
        }
    }
}

public static class SourceLines
{
    public static string[] Split(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
