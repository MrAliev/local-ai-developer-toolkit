using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Semantics;

public sealed record LiveSemanticNavigationResult(
    bool Handled,
    IReadOnlyList<SemanticLocation> Locations);

public interface ILiveSemanticNavigation
{
    Task<LiveSemanticNavigationResult> GoToDefinitionAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        CancellationToken cancellationToken);

    Task<LiveSemanticNavigationResult> FindReferencesAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        CancellationToken cancellationToken);

    Task<LiveSemanticNavigationResult> FindImplementationsAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        CancellationToken cancellationToken);
}

/// <summary>Turns authoritative results for open LSP documents into precise semantic locations.</summary>
public sealed class LiveSemanticNavigationOverlay(LanguageServerSessionManager sessions)
    : ILiveSemanticNavigation
{
    private readonly LanguageServerSessionManager _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));

    public async Task<LiveSemanticNavigationResult> GoToDefinitionAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        CancellationToken cancellationToken)
    {
        if (!_sessions.IsOpen(workspaceRoot, documentPath))
        {
            return new(false, []);
        }

        var locations = await _sessions.GoToDefinitionAsync(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            cancellationToken);
        return new(true, Convert(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            locations,
            SemanticOccurrenceRoles.Definition));
    }

    public async Task<LiveSemanticNavigationResult> FindReferencesAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        CancellationToken cancellationToken)
    {
        if (!_sessions.IsOpen(workspaceRoot, documentPath))
        {
            return new(false, []);
        }

        var locations = await _sessions.FindReferencesAsync(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            includeDefinition,
            cancellationToken);
        return new(true, Convert(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            locations,
            SemanticOccurrenceRoles.Reference));
    }

    public async Task<LiveSemanticNavigationResult> FindImplementationsAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        CancellationToken cancellationToken)
    {
        if (!_sessions.IsOpen(workspaceRoot, documentPath))
        {
            return new(false, []);
        }

        var locations = await _sessions.FindImplementationsAsync(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            cancellationToken);
        return new(true, Convert(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            locations,
            SemanticOccurrenceRoles.Definition));
    }

    private static IReadOnlyList<SemanticLocation> Convert(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        IReadOnlyList<LspLocation> locations,
        SemanticOccurrenceRoles roles)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var symbolId = LiveSymbolId(documentPath, line, utf16Column);
        var converted = new List<SemanticLocation>(locations.Count);
        foreach (var location in locations)
        {
            if (!location.Uri.IsFile)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(location.Uri.LocalPath);
            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                continue;
            }

            converted.Add(new SemanticLocation(
                relative,
                location.Range,
                symbolId,
                roles,
                NavigationPrecision.Precise));
        }

        return converted
            .Distinct()
            .OrderBy(location => location.DocumentPath, StringComparer.Ordinal)
            .ThenBy(location => location.Range.StartLine)
            .ThenBy(location => location.Range.StartCharacter)
            .ToList();
    }

    private static string LiveSymbolId(string documentPath, int line, int column)
    {
        var input = $"{documentPath.Replace('\\', '/')}\n{line}\n{column}";
        return "lsp local " + System.Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
