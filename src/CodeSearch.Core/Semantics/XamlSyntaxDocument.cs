using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Core.Semantics;

public sealed record XamlAttributeSyntax(
    string Name,
    string Value,
    SourceRange NameRange,
    SourceRange ValueRange);

public sealed record XamlElementSyntax(
    string Name,
    SourceRange NameRange,
    IReadOnlyList<XamlAttributeSyntax> Attributes,
    IReadOnlyDictionary<string, string> Namespaces);

/// <summary>A small lossless XAML lexer: it preserves attribute value ranges exactly.</summary>
public sealed record XamlSyntaxDocument(
    string DocumentPath,
    string Text,
    IReadOnlyList<XamlElementSyntax> Elements)
{
    public static XamlSyntaxDocument Parse(string documentPath, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(text);
        var source = SourceText.From(text);
        var elements = new List<XamlElementSyntax>();
        var namespaceStack = new Stack<Dictionary<string, string>>();
        var elementStack = new Stack<string>();
        var index = 0;

        while ((index = text.IndexOf('<', index)) >= 0)
        {
            if (StartsWith(text, index, "<!--"))
            {
                index = SkipThrough(text, index + 4, "-->");
                continue;
            }

            if (StartsWith(text, index, "<![CDATA["))
            {
                index = SkipThrough(text, index + 9, "]]>");
                continue;
            }

            if (StartsWith(text, index, "<?"))
            {
                index = SkipThrough(text, index + 2, "?>");
                continue;
            }

            if (StartsWith(text, index, "<!"))
            {
                index = SkipThrough(text, index + 2, ">");
                continue;
            }

            if (StartsWith(text, index, "</"))
            {
                index = SkipThrough(text, index + 2, ">");
                if (elementStack.Count > 0)
                {
                    elementStack.Pop();
                    namespaceStack.Pop();
                }

                continue;
            }

            var cursor = index + 1;
            SkipWhitespace(text, ref cursor);
            var nameStart = cursor;
            ReadName(text, ref cursor);
            if (cursor == nameStart)
            {
                throw Invalid(documentPath, index, "element name");
            }

            var elementName = text[nameStart..cursor];
            var attributes = new List<XamlAttributeSyntax>();
            var selfClosing = false;
            while (cursor < text.Length)
            {
                SkipWhitespace(text, ref cursor);
                if (cursor >= text.Length)
                {
                    throw Invalid(documentPath, index, "closing '>'");
                }

                if (text[cursor] == '>')
                {
                    cursor++;
                    break;
                }

                if (text[cursor] == '/' && cursor + 1 < text.Length && text[cursor + 1] == '>')
                {
                    cursor += 2;
                    selfClosing = true;
                    break;
                }

                var attributeNameStart = cursor;
                ReadName(text, ref cursor);
                if (cursor == attributeNameStart)
                {
                    throw Invalid(documentPath, cursor, "attribute name");
                }

                var attributeName = text[attributeNameStart..cursor];
                SkipWhitespace(text, ref cursor);
                if (cursor >= text.Length || text[cursor] != '=')
                {
                    throw Invalid(documentPath, cursor, "'='");
                }

                cursor++;
                SkipWhitespace(text, ref cursor);
                if (cursor >= text.Length || text[cursor] is not ('\'' or '"'))
                {
                    throw Invalid(documentPath, cursor, "quoted attribute value");
                }

                var quote = text[cursor++];
                var valueStart = cursor;
                var valueEnd = text.IndexOf(quote, cursor);
                if (valueEnd < 0)
                {
                    throw Invalid(documentPath, cursor, "attribute closing quote");
                }

                attributes.Add(new XamlAttributeSyntax(
                    attributeName,
                    text[valueStart..valueEnd],
                    Range(source, attributeNameStart, cursor: attributeNameStart + attributeName.Length),
                    Range(source, valueStart, valueEnd)));
                cursor = valueEnd + 1;
            }

            var namespaces = namespaceStack.Count == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(namespaceStack.Peek(), StringComparer.Ordinal);
            foreach (var attribute in attributes)
            {
                if (string.Equals(attribute.Name, "xmlns", StringComparison.Ordinal))
                {
                    namespaces[string.Empty] = attribute.Value;
                }
                else if (attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    namespaces[attribute.Name[6..]] = attribute.Value;
                }
            }

            elements.Add(new XamlElementSyntax(
                elementName,
                Range(source, nameStart, cursor: nameStart + elementName.Length),
                attributes,
                namespaces));
            if (!selfClosing)
            {
                elementStack.Push(elementName);
                namespaceStack.Push(namespaces);
            }

            index = cursor;
        }

        return new XamlSyntaxDocument(documentPath.Replace('\\', '/'), text, elements);
    }

    private static void ReadName(string text, ref int cursor)
    {
        while (cursor < text.Length &&
               !char.IsWhiteSpace(text[cursor]) &&
               text[cursor] is not ('=' or '>' or '/' or '<'))
        {
            cursor++;
        }
    }

    private static void SkipWhitespace(string text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }
    }

    private static int SkipThrough(string text, int start, string terminator)
    {
        var end = text.IndexOf(terminator, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException($"Unterminated XAML construct '{terminator}'.");
        }

        return end + terminator.Length;
    }

    private static bool StartsWith(string text, int index, string value) =>
        text.AsSpan(index).StartsWith(value, StringComparison.Ordinal);

    private static SourceRange Range(SourceText text, int start, int cursor)
    {
        var span = text.Lines.GetLinePositionSpan(TextSpan.FromBounds(start, cursor));
        return new SourceRange(
            span.Start.Line,
            span.Start.Character,
            span.End.Line,
            span.End.Character);
    }

    private static InvalidDataException Invalid(string path, int offset, string expected) =>
        new($"Malformed XAML '{path}' at offset {offset}; expected {expected}.");
}
