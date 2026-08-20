using System.Text;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Core.Chunking;

/// <summary>
/// Cuts a file on the definition boundaries the semantic index reports, and keeps the sliding
/// window for every line no definition covers.
/// </summary>
/// <remarks>
/// This is what a TypeScript or Python hit stops being a line range and starts being a symbol.
/// The boundaries are not guessed: they come from `enclosing_range`, which the external indexers
/// report and which issue #87 measured before any of this was written.
///
/// Two rules keep the corpus honest. Every line of the file lands in exactly one chunk, so
/// nothing goes unindexed and nothing is embedded twice — a definition that contains others
/// keeps its own lines and lists its children by their first line, the way the Roslyn chunker
/// lists a type's members rather than repeating their bodies. And a region no definition covers
/// is windowed rather than attached to a neighbour, because a chunk that spans "the end of one
/// function and the start of the next" is the thing this exists to stop producing.
/// </remarks>
public sealed class SymbolAwareChunker : IChunker
{
    private const int WindowLines = 60;
    private const int OverlapLines = 12;

    private readonly IReadOnlyList<SymbolDefinition> _definitions;
    private readonly GenericTextChunker _fallback = new();

    public SymbolAwareChunker(IReadOnlyList<SymbolDefinition> definitions) =>
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public IEnumerable<Chunk> Split(string relPath, string content)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        ArgumentNullException.ThrowIfNull(content);

        if (content.IndexOf('\0') >= 0)
        {
            return [];
        }

        var lines = SourceLines.Split(content);
        var spans = Normalize(_definitions, lines);
        return spans.Count == 0
            // Nothing usable: a file whose definitions all sit inside function bodies, or one the
            // adapter did not cover. It gets exactly what it got before this class existed.
            ? _fallback.Split(relPath, content)
            : Emit(relPath, lines, spans).ToList();
    }

    private IEnumerable<Chunk> Emit(string relPath, string[] lines, List<Span> spans)
    {
        var ns = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? string.Empty;
        var covered = new bool[lines.Length + 1];

        foreach (var span in spans)
        {
            var children = spans
                .Where(candidate => candidate.Parent == span)
                .OrderBy(candidate => candidate.Start)
                .ToList();
            var body = new List<string>();
            for (var line = span.Start; line <= span.End; line++)
            {
                var child = children.FirstOrDefault(
                    candidate => line >= candidate.Start && line <= candidate.End);
                if (child is null)
                {
                    body.Add(lines[line - 1]);
                    covered[line] = true;
                    continue;
                }

                // One line per nested definition instead of its body: the parent still answers
                // "what does this class contain", and the nested definition answers for itself.
                if (line == child.Start)
                {
                    body.Add(lines[line - 1]);
                }
            }

            var header = new StringBuilder()
                .Append("File: ").AppendLine(relPath);
            if (ns.Length > 0)
            {
                header.Append("Namespace: ").AppendLine(ns);
            }

            if (span.Parent is { } parent)
            {
                header.Append("Within: ").AppendLine(parent.Symbol);
            }

            foreach (var chunk in ChunkSplitter.Split(
                relPath,
                children.Count > 0 || span.IsContainer ? ChunkKind.Type : ChunkKind.Method,
                span.Symbol,
                span.Signature,
                ns,
                header.ToString(),
                body.ToArray(),
                span.Start,
                span.End))
            {
                yield return chunk;
            }
        }

        // Imports, module-level statements, the gap between two functions: windowed, and named
        // for the file the way they always were, so a hit there still reads as one.
        foreach (var chunk in WindowUncoveredRegions(relPath, lines, covered, ns))
        {
            yield return chunk;
        }
    }

    private static IEnumerable<Chunk> WindowUncoveredRegions(
        string relPath,
        string[] lines,
        bool[] covered,
        string ns)
    {
        var fileName = Path.GetFileName(relPath);
        var regions = new List<(int Start, int End)>();
        var start = 0;
        for (var line = 1; line <= lines.Length; line++)
        {
            if (!covered[line])
            {
                start = start == 0 ? line : start;
                continue;
            }

            if (start != 0)
            {
                regions.Add((start, line - 1));
                start = 0;
            }
        }

        if (start != 0)
        {
            regions.Add((start, lines.Length));
        }

        var windows = regions
            .SelectMany(region => Windows(region.Start, region.End))
            .Where(window => !string.IsNullOrWhiteSpace(
                string.Join("\n", lines[(window.Start - 1)..window.End])))
            .ToList();

        for (var index = 0; index < windows.Count; index++)
        {
            var (windowStart, windowEnd) = windows[index];
            var body = ChunkSplitter.Truncate(
                string.Join("\n", lines[(windowStart - 1)..windowEnd]),
                ChunkLimits.MaxChars);
            var text = new StringBuilder()
                .Append("File: ").AppendLine(relPath)
                .Append("Lines: ").Append(windowStart).Append('-').Append(windowEnd).AppendLine()
                .AppendLine()
                .Append(body)
                .ToString();

            yield return new Chunk
            {
                RelPath = relPath,
                Kind = ChunkKind.Text,
                Symbol = windows.Count > 1
                    ? $"{fileName} [{index + 1}/{windows.Count}]"
                    : fileName,
                Signature = $"{relPath}:{windowStart}-{windowEnd}",
                Namespace = ns,
                StartLine = windowStart,
                EndLine = windowEnd,
                EmbedText = text,
            };
        }
    }

    private static IEnumerable<(int Start, int End)> Windows(int start, int end)
    {
        var step = WindowLines - OverlapLines;
        for (var line = start; line <= end; line += step)
        {
            var last = Math.Min(line + WindowLines - 1, end);
            yield return (line, last);
            if (last == end)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Turns reported ranges into a nesting of line spans, dropping what cannot be cut on.
    /// </summary>
    private static List<Span> Normalize(
        IReadOnlyList<SymbolDefinition> definitions,
        string[] lines)
    {
        var spans = new List<Span>();
        foreach (var definition in definitions)
        {
            var start = definition.Body.StartLine + 1;
            var end = definition.Body.EndLine + 1;
            if (start < 1 || end < start || end > lines.Length)
            {
                continue;
            }

            // The module symbol of a TypeScript document reports the whole file as its body.
            // Kept, it would give every file one chunk containing everything and nest the real
            // definitions inside it.
            if (start == 1 && end >= lines.Length && definitions.Count > 1)
            {
                continue;
            }

            var name = Spelling(lines, definition.Name);
            if (name.Length == 0)
            {
                continue;
            }

            spans.Add(new Span(start, end, name, Signature(lines, start)));
        }

        // Deduplicate by span: two occurrences reporting the same body are the same definition
        // seen twice, and emitting both would embed those lines twice.
        var distinct = spans
            .GroupBy(span => (span.Start, span.End))
            .Select(group => group.First())
            .OrderBy(span => span.Start)
            .ThenByDescending(span => span.End)
            .ToList();

        foreach (var span in distinct)
        {
            span.Parent = distinct
                .Where(candidate =>
                    candidate != span &&
                    candidate.Start <= span.Start &&
                    candidate.End >= span.End)
                .OrderByDescending(candidate => candidate.Start)
                .FirstOrDefault();
        }

        foreach (var span in distinct)
        {
            span.Symbol = span.Parent is null
                ? span.Name
                : $"{span.Parent.Symbol}.{span.Name}";
        }

        return distinct;
    }

    private static string Signature(string[] lines, int start)
    {
        var line = lines[start - 1].Trim();
        return line.Length <= 200 ? line : line[..200];
    }

    private static string Spelling(string[] lines, SourceRange range)
    {
        if (range.StartLine != range.EndLine ||
            range.StartLine < 0 ||
            range.StartLine >= lines.Length)
        {
            return string.Empty;
        }

        var line = lines[range.StartLine];
        var end = Math.Min(range.EndCharacter, line.Length);
        return range.StartCharacter < 0 || range.StartCharacter >= end
            ? string.Empty
            : line[range.StartCharacter..end];
    }

    private sealed class Span(int start, int end, string name, string signature)
    {
        public int Start { get; } = start;

        public int End { get; } = end;

        public string Name { get; } = name;

        public string Signature { get; } = signature;

        public Span? Parent { get; set; }

        public string Symbol { get; set; } = name;

        /// <summary>
        /// A definition that reads as a container even with nothing nested inside it, so an empty
        /// class is still a type rather than a method.
        /// </summary>
        public bool IsContainer =>
            Signature.StartsWith("class ", StringComparison.Ordinal) ||
            Signature.StartsWith("interface ", StringComparison.Ordinal) ||
            Signature.Contains(" class ", StringComparison.Ordinal) ||
            Signature.Contains(" interface ", StringComparison.Ordinal);
    }
}
