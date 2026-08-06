using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Indexing;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Core.Semantics;

public interface IHeuristicSemanticNavigation
{
    Task<IReadOnlyList<SemanticLocation>> GoToDefinitionAsync(
        string repositoryRoot,
        string documentPath,
        int line,
        int utf16Column,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SemanticLocation>> FindReferencesAsync(
        string repositoryRoot,
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        CancellationToken cancellationToken);
}

/// <summary>
/// Bounded literal fallback for unsupported languages. It never claims compiler symbol identity.
/// </summary>
public sealed class HeuristicSemanticNavigation(HeuristicNavigationPolicyStore policyStore)
    : IHeuristicSemanticNavigation
{
    private static readonly HashSet<string> DeclarationKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "interface", "enum", "struct", "record", "def", "function", "type",
        "namespace", "module", "const", "let", "var", "trait", "protocol",
    };

    private readonly HeuristicNavigationPolicyStore _policyStore =
        policyStore ?? throw new ArgumentNullException(nameof(policyStore));

    public Task<IReadOnlyList<SemanticLocation>> GoToDefinitionAsync(
        string repositoryRoot,
        string documentPath,
        int line,
        int utf16Column,
        CancellationToken cancellationToken) =>
        SearchAsync(
            repositoryRoot,
            documentPath,
            line,
            utf16Column,
            definitionsOnly: true,
            includeDefinition: true,
            cancellationToken);

    public Task<IReadOnlyList<SemanticLocation>> FindReferencesAsync(
        string repositoryRoot,
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        CancellationToken cancellationToken) =>
        SearchAsync(
            repositoryRoot,
            documentPath,
            line,
            utf16Column,
            definitionsOnly: false,
            includeDefinition,
            cancellationToken);

    private async Task<IReadOnlyList<SemanticLocation>> SearchAsync(
        string repositoryRoot,
        string documentPath,
        int line,
        int utf16Column,
        bool definitionsOnly,
        bool includeDefinition,
        CancellationToken cancellationToken)
    {
        var policy = _policyStore.Read();
        if (!policy.Enabled)
        {
            return [];
        }

        var root = Path.GetFullPath(repositoryRoot);
        if (!SafeSourcePath.TryResolveFile(root, documentPath, out var queryPath, out _))
        {
            return [];
        }

        string queryText;
        try
        {
            queryText = await File.ReadAllTextAsync(queryPath, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var identifier = IdentifierAt(queryText, line, utf16Column);
        if (identifier is null || identifier.Length > policy.MaximumIdentifierLength)
        {
            return [];
        }

        var comparison = policy.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var symbolId = SymbolId(identifier, policy.CaseSensitive);
        var results = new List<SemanticLocation>();
        foreach (var relativePath in FileScanner.Enumerate(root).Take(policy.MaximumFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeSourcePath.TryResolveFile(root, relativePath, out var fullPath, out _))
            {
                continue;
            }

            try
            {
                if (new FileInfo(fullPath).Length > policy.MaximumFileBytes)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            string text;
            try
            {
                text = string.Equals(fullPath, queryPath, PathComparison)
                    ? queryText
                    : await File.ReadAllTextAsync(fullPath, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var source = SourceText.From(text);
            var searchFrom = 0;
            while (searchFrom <= text.Length - identifier.Length &&
                   results.Count < policy.MaximumResults)
            {
                var offset = text.IndexOf(identifier, searchFrom, comparison);
                if (offset < 0)
                {
                    break;
                }

                searchFrom = offset + Math.Max(1, identifier.Length);
                if (!HasTokenBoundaries(text, offset, identifier.Length))
                {
                    continue;
                }

                var range = source.Lines.GetLinePositionSpan(
                    new TextSpan(offset, identifier.Length));
                var sourceRange = new SourceRange(
                    range.Start.Line,
                    range.Start.Character,
                    range.End.Line,
                    range.End.Character);
                var definition = IsDeclaration(
                    relativePath,
                    text,
                    source,
                    offset,
                    identifier.Length);
                if (definitionsOnly && !definition || !includeDefinition && definition)
                {
                    continue;
                }

                results.Add(new SemanticLocation(
                    relativePath.Replace('\\', '/'),
                    sourceRange,
                    symbolId,
                    definition
                        ? SemanticOccurrenceRoles.Definition
                        : SemanticOccurrenceRoles.Reference,
                    NavigationPrecision.Heuristic));
            }

            if (results.Count >= policy.MaximumResults)
            {
                break;
            }
        }

        return results
            .OrderBy(location => location.DocumentPath, StringComparer.Ordinal)
            .ThenBy(location => location.Range.StartLine)
            .ThenBy(location => location.Range.StartCharacter)
            .ToList();
    }

    private static string? IdentifierAt(string text, int line, int column)
    {
        if (line < 0 || column < 0)
        {
            throw new ArgumentOutOfRangeException(line < 0 ? nameof(line) : nameof(column));
        }

        var source = SourceText.From(text);
        if (line >= source.Lines.Count)
        {
            return null;
        }

        var sourceLine = source.Lines[line];
        if (column > sourceLine.Span.Length)
        {
            return null;
        }

        var offset = sourceLine.Start + column;
        if (offset == sourceLine.End && offset > sourceLine.Start && IsIdentifierCharacter(text[offset - 1]))
        {
            offset--;
        }

        if (offset >= sourceLine.End || !IsIdentifierCharacter(text[offset]))
        {
            return null;
        }

        var start = offset;
        while (start > sourceLine.Start && IsIdentifierCharacter(text[start - 1])) start--;
        var end = offset + 1;
        while (end < sourceLine.End && IsIdentifierCharacter(text[end])) end++;
        return text[start..end];
    }

    private static bool HasTokenBoundaries(string text, int offset, int length) =>
        (offset == 0 || !IsIdentifierCharacter(text[offset - 1])) &&
        (offset + length == text.Length || !IsIdentifierCharacter(text[offset + length]));

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$' or '-';

    private static bool IsDeclaration(
        string relativePath,
        string text,
        SourceText source,
        int offset,
        int length)
    {
        var line = source.Lines.GetLineFromPosition(offset);
        var before = text[line.Start..offset];
        var after = text[(offset + length)..line.End];
        var previousWord = before.TrimEnd()
            .Split(
                [' ', '\t', '(', ')', '{', '}', '[', ']', ':', ';', ',', '='],
                StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (previousWord is not null && DeclarationKeywords.Contains(previousWord))
        {
            return true;
        }

        var compactBefore = before.TrimEnd();
        if (new[] { "id=\"", "name=\"", "x:Name=\"", "x:Key=\"" }
            .Any(prefix => compactBefore.EndsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var extension = Path.GetExtension(relativePath);
        if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase) &&
            compactBefore.Length > 0 && compactBefore[^1] is '.' or '#')
        {
            return true;
        }

        var trimmedAfter = after.TrimStart();
        if (trimmedAfter.StartsWith('"'))
        {
            trimmedAfter = trimmedAfter[1..].TrimStart();
        }

        return trimmedAfter.StartsWith(':') &&
               (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static string SymbolId(string identifier, bool caseSensitive)
    {
        var canonical = caseSensitive ? identifier : identifier.ToUpperInvariant();
        return "heuristic local " + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
