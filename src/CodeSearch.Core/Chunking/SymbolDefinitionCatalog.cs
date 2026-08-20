using CodeSearch.Core.Semantics;

namespace CodeSearch.Core.Chunking;

/// <summary>
/// One definition as the chunker sees it: where its name is written, and where its body runs.
/// </summary>
/// <remarks>
/// The name range is carried alongside the body because the body alone cannot be named. A symbol
/// id is composed by the indexer — <c>shapes/Invoice#total().</c> from scip-python, <c>local 3</c>
/// for anything scip-typescript declares inside a function body — and the display name is empty
/// more often than not. The identifier as written in the source is always there, and slicing the
/// name range out of the file is the one way to get it that works for both languages.
/// </remarks>
public sealed record SymbolDefinition(SourceRange Name, SourceRange Body);

/// <summary>
/// Definition bodies per file, taken from the semantic index that was built before embedding.
/// </summary>
public sealed class SymbolDefinitionCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SymbolDefinition>> _byPath;

    private SymbolDefinitionCatalog(
        IReadOnlyDictionary<string, IReadOnlyList<SymbolDefinition>> byPath) =>
        _byPath = byPath;

    /// <summary>
    /// What a build with no semantic phase, a disabled adapter or a missing indexer produces.
    /// Every file then goes to the chunker it went to before this existed.
    /// </summary>
    public static SymbolDefinitionCatalog Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<SymbolDefinition>>(StringComparer.OrdinalIgnoreCase));

    public bool IsEmpty => _byPath.Count == 0;

    public IReadOnlyList<SymbolDefinition> For(string relPath) =>
        _byPath.TryGetValue(Normalize(relPath), out var definitions)
            ? definitions
            : [];

    /// <summary>
    /// Collects every definition occurrence that reports a body.
    /// </summary>
    /// <remarks>
    /// References are skipped because they are not definitions, and definitions without a body
    /// span are skipped because there is nothing to cut on: scip-typescript reports none for
    /// anything declared inside a function body, and those regions stay with the sliding window
    /// rather than being guessed at.
    /// </remarks>
    public static SymbolDefinitionCatalog FromSemanticIndex(SemanticIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var byPath = new Dictionary<string, List<SymbolDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in index.Occurrences)
        {
            if (!occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) ||
                occurrence.EnclosingRange is not { } body)
            {
                continue;
            }

            var path = Normalize(occurrence.DocumentPath);
            if (!byPath.TryGetValue(path, out var definitions))
            {
                definitions = [];
                byPath[path] = definitions;
            }

            definitions.Add(new SymbolDefinition(occurrence.Range, body));
        }

        return new SymbolDefinitionCatalog(
            byPath.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<SymbolDefinition>)pair.Value,
                StringComparer.OrdinalIgnoreCase));
    }

    // The semantic index stores forward slashes and the scanner hands out platform separators.
    // A catalog keyed one way and queried the other silently degrades every file to the window,
    // which is the failure that looks exactly like "the feature does nothing".
    private static string Normalize(string relPath) => relPath.Replace('\\', '/');
}
