using CodeSearch.Core.Indexing;
using LocalAi.Repository;

namespace CodeSearch.Core.Semantics;

public sealed record SemanticNavigationContext(
    SemanticNavigationService Service,
    SemanticSnapshotIdentity Snapshot);

public sealed class SemanticNavigationNotReadyException(string message)
    : InvalidOperationException(message);

/// <summary>
/// A navigation answer together with the reason it is not the precise one, when it is not.
///
/// The gateway already knew why: a generation published before semantic indexing existed has no
/// <c>semantic.sidx</c>, and loading it raises an exception naming the generation and saying to
/// rebuild it. That exception was then swallowed to reach the text fallback, so a repository that
/// had simply not been re-synced answered <c>find_references</c> with README and docs matches and
/// no indication that anything was missing. Every hit was honestly tagged Heuristic; nothing said
/// why, or what to do about it.
/// </summary>
public sealed record SemanticNavigationOutcome(
    IReadOnlyList<SemanticLocation> Locations,
    string? Degradation)
{
    public static SemanticNavigationOutcome Precise(
        IReadOnlyList<SemanticLocation> locations) =>
        new(locations, null);
}

/// <summary>Loads the exact current semantic generation and executes snapshot-bound queries.</summary>
public sealed class SemanticNavigationGateway
{
    private readonly Func<string?, SemanticNavigationContext> _contextFactory;
    private readonly ILiveSemanticNavigation? _liveNavigation;
    private readonly IHeuristicSemanticNavigation? _heuristicNavigation;

    public SemanticNavigationGateway(
        Func<string?, SemanticNavigationContext>? contextFactory = null,
        ILiveSemanticNavigation? liveNavigation = null,
        IHeuristicSemanticNavigation? heuristicNavigation = null)
    {
        _contextFactory = contextFactory ?? LoadCurrent;
        _liveNavigation = liveNavigation;
        _heuristicNavigation = heuristicNavigation;
    }

    public IReadOnlyList<SemanticLocation> GoToDefinition(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null)
    {
        return GoToDefinitionAsync(documentPath, line, utf16Column, root)
            .GetAwaiter().GetResult();
    }

    public async Task<IReadOnlyList<SemanticLocation>> GoToDefinitionAsync(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null,
        CancellationToken cancellationToken = default) =>
        (await ResolveDefinitionAsync(
            documentPath,
            line,
            utf16Column,
            root,
            cancellationToken)).Locations;

    public SemanticNavigationOutcome ResolveDefinition(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null) =>
        ResolveDefinitionAsync(documentPath, line, utf16Column, root)
            .GetAwaiter().GetResult();

    public async Task<SemanticNavigationOutcome> ResolveDefinitionAsync(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        if (_liveNavigation is not null)
        {
            var workingRoot = ResolveLiveRoot(root);
            var live = await _liveNavigation.GoToDefinitionAsync(
                workingRoot,
                documentPath,
                line,
                utf16Column,
                cancellationToken);
            if (live.Handled)
            {
                return SemanticNavigationOutcome.Precise(live.Locations);
            }
        }

        string degradation;
        try
        {
            var context = _contextFactory(root);
            var precise = context.Service.GoToDefinition(
                documentPath,
                line,
                utf16Column,
                context.Snapshot);
            if (precise.Count > 0 || _heuristicNavigation is null)
            {
                return SemanticNavigationOutcome.Precise(precise);
            }

            degradation = EmptyPreciseResult;
        }
        catch (SemanticNavigationNotReadyException exception) when (
            _heuristicNavigation is not null)
        {
            degradation = exception.Message;
        }

        return new SemanticNavigationOutcome(
            await _heuristicNavigation!.GoToDefinitionAsync(
                ResolveLiveRoot(root),
                documentPath,
                line,
                utf16Column,
                cancellationToken),
            degradation);
    }

    public IReadOnlyList<SemanticLocation> FindReferences(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        string? root = null)
    {
        return FindReferencesAsync(
            documentPath,
            line,
            utf16Column,
            includeDefinition,
            root).GetAwaiter().GetResult();
    }

    public async Task<IReadOnlyList<SemanticLocation>> FindReferencesAsync(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        string? root = null,
        CancellationToken cancellationToken = default) =>
        (await ResolveReferencesAsync(
            documentPath,
            line,
            utf16Column,
            includeDefinition,
            root,
            cancellationToken)).Locations;

    public SemanticNavigationOutcome ResolveReferences(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        string? root = null) =>
        ResolveReferencesAsync(documentPath, line, utf16Column, includeDefinition, root)
            .GetAwaiter().GetResult();

    public async Task<SemanticNavigationOutcome> ResolveReferencesAsync(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        if (_liveNavigation is not null)
        {
            var workingRoot = ResolveLiveRoot(root);
            var live = await _liveNavigation.FindReferencesAsync(
                workingRoot,
                documentPath,
                line,
                utf16Column,
                includeDefinition,
                cancellationToken);
            if (live.Handled)
            {
                return SemanticNavigationOutcome.Precise(live.Locations);
            }
        }

        string degradation;
        try
        {
            var context = _contextFactory(root);
            var precise = context.Service.FindReferences(
                documentPath,
                line,
                utf16Column,
                includeDefinition,
                context.Snapshot);
            if (precise.Count > 0 || _heuristicNavigation is null)
            {
                return SemanticNavigationOutcome.Precise(precise);
            }

            degradation = EmptyPreciseResult;
        }
        catch (SemanticNavigationNotReadyException exception) when (
            _heuristicNavigation is not null)
        {
            degradation = exception.Message;
        }

        return new SemanticNavigationOutcome(
            await _heuristicNavigation!.FindReferencesAsync(
                ResolveLiveRoot(root),
                documentPath,
                line,
                utf16Column,
                includeDefinition,
                cancellationToken),
            degradation);
    }

    public IReadOnlyList<SemanticLocation> FindImplementations(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null) =>
        FindImplementationsAsync(documentPath, line, utf16Column, root)
            .GetAwaiter().GetResult();

    public async Task<IReadOnlyList<SemanticLocation>> FindImplementationsAsync(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        if (_liveNavigation is not null)
        {
            var live = await _liveNavigation.FindImplementationsAsync(
                ResolveLiveRoot(root),
                documentPath,
                line,
                utf16Column,
                cancellationToken);
            if (live.Handled)
            {
                return live.Locations;
            }
        }

        var context = _contextFactory(root);
        return context.Service.FindImplementations(
            documentPath,
            line,
            utf16Column,
            context.Snapshot);
    }

    public IReadOnlyList<SemanticRelatedLocation> FindRelationships(
        string documentPath,
        int line,
        int utf16Column,
        SemanticRelationshipDirection direction,
        SemanticRelationshipKind? kind = null,
        string? root = null)
    {
        var context = _contextFactory(root);
        return context.Service.FindRelationships(
            documentPath,
            line,
            utf16Column,
            direction,
            kind,
            context.Snapshot);
    }

    /// <summary>
    /// The other way a caller ends up on text matches: the semantic index is present and current,
    /// and simply has nothing at that position. Worth distinguishing from a missing index, because
    /// only one of the two is fixed by re-syncing.
    /// </summary>
    private const string EmptyPreciseResult =
        "The semantic index has no symbol at this position, so these are bounded text matches " +
        "rather than resolved references.";

    private static string ResolveLiveRoot(string? root) =>
        root is null
            ? RepoLocator.ResolveWorkingRoot(null)
            : Path.GetFullPath(root);

    private static SemanticNavigationContext LoadCurrent(string? root)
    {
        var workingRoot = RepoLocator.ResolveWorkingRoot(root);
        var identity = RuntimeIndexLayout.Inspect(workingRoot);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        var current = store.ReadCurrent()
            ?? throw new SemanticNavigationNotReadyException(
                "No current repository generation is published. Run localai sync first.");
        var manifest = store.ReadManifest(current.GenerationId);
        if (manifest.SemanticIndexFile is null)
        {
            throw new SemanticNavigationNotReadyException(
                $"Generation '{current.GenerationId}' has no semantic.sidx. Rebuild it with semantic indexing enabled.");
        }

        var usesBaseSnapshot =
            string.Equals(identity.HeadTree, manifest.Identity.DevTree, StringComparison.Ordinal) &&
            identity.DirtyHash is null;
        SemanticIndex semanticIndex;
        try
        {
            var baseIndex = SemanticIndex.Load(store.SemanticIndexPath(current.GenerationId));
            semanticIndex = usesBaseSnapshot
                ? baseIndex
                : SemanticIndexOverlay.Load(
                    RuntimeIndexLayout.SemanticOverlayPath(identity, current.GenerationId))
                    .Materialize(baseIndex);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new SemanticNavigationNotReadyException(
                $"Semantic index for the current worktree snapshot is unavailable: {exception.Message}. " +
                "Run localai sync to build it.");
        }

        return new SemanticNavigationContext(
            new SemanticNavigationService(semanticIndex),
            new SemanticSnapshotIdentity(
                identity.RepositoryId,
                current.GenerationId,
                identity.HeadTree,
                identity.DirtyHash));
    }
}
