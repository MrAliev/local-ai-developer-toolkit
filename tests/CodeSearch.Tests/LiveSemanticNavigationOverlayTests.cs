using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public class LiveSemanticNavigationOverlayTests
{
    [Fact]
    public async Task OpenDocumentLspResultsArePreciseAndRepositoryRelative()
    {
        var root = TempRoot();
        var client = new FakeClient
        {
            Definitions =
            [
                new LspLocation(FileUri(Path.Combine(root, "src", "Def.ts")),
                    new SourceRange(2, 3, 2, 6)),
                new LspLocation(FileUri(Path.Combine(Path.GetTempPath(), "outside.ts")),
                    new SourceRange(0, 0, 0, 1)),
            ],
        };
        await using var sessions = new LanguageServerSessionManager((_, _) => client);
        await sessions.OpenOrUpdateAsync(
            root, "src/Use.ts", "typescript", 1, "run();", Ct);
        var overlay = new LiveSemanticNavigationOverlay(sessions);

        var result = await overlay.GoToDefinitionAsync(root, "src/Use.ts", 0, 1, Ct);

        Assert.True(result.Handled);
        var location = Assert.Single(result.Locations);
        Assert.Equal("src/Def.ts", location.DocumentPath);
        Assert.Equal(NavigationPrecision.Precise, location.Precision);
        Assert.True(location.Roles.HasFlag(SemanticOccurrenceRoles.Definition));
        Assert.StartsWith("lsp local ", location.SymbolId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedDocumentIsNotHandledByTheLiveOverlay()
    {
        var root = TempRoot();
        await using var sessions = new LanguageServerSessionManager((_, _) => new FakeClient());
        var overlay = new LiveSemanticNavigationOverlay(sessions);

        var result = await overlay.GoToDefinitionAsync(root, "closed.ts", 0, 0, Ct);

        Assert.False(result.Handled);
        Assert.Empty(result.Locations);
    }

    [Fact]
    public async Task GatewayPrefersAnAuthoritativeEmptyLiveResultOverSidx()
    {
        var persistentCalled = false;
        var gateway = new SemanticNavigationGateway(
            _ =>
            {
                persistentCalled = true;
                throw new InvalidOperationException("SIDX should not be queried.");
            },
            new FakeLiveNavigation(new LiveSemanticNavigationResult(true, [])));

        var result = await gateway.GoToDefinitionAsync(
            "Use.ts", 0, 0, TempRoot(), Ct);

        Assert.Empty(result);
        Assert.False(persistentCalled);
    }

    [Fact]
    public async Task GatewayFallsBackToSidxWhenDocumentIsNotOpen()
    {
        var index = PersistentIndex();
        var snapshot = new SemanticSnapshotIdentity("repository", "generation", "tree", null);
        var gateway = new SemanticNavigationGateway(
            _ => new SemanticNavigationContext(new SemanticNavigationService(index), snapshot),
            new FakeLiveNavigation(new LiveSemanticNavigationResult(false, [])));

        var result = await gateway.GoToDefinitionAsync(
            "Use.ts", 0, 1, TempRoot(), Ct);

        Assert.Single(result);
        Assert.Equal("Def.ts", result[0].DocumentPath);
    }

    private static SemanticIndex PersistentIndex() =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            IndexedAtUtc = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc),
            Documents = [Document("Def.ts", 1), Document("Use.ts", 2)],
            Symbols =
            [
                new SemanticSymbol
                {
                    Id = "symbol",
                    DisplayName = "run",
                    Kind = SemanticSymbolKind.Method,
                },
            ],
            Occurrences =
            [
                Occurrence("Def.ts", SemanticOccurrenceRoles.Definition),
                Occurrence("Use.ts", SemanticOccurrenceRoles.Reference),
            ],
            Relationships = [],
        };

    private static SemanticDocument Document(string path, byte value) =>
        new() { RelPath = path, Hash = Enumerable.Repeat(value, 32).ToArray() };

    private static SemanticOccurrence Occurrence(string path, SemanticOccurrenceRoles roles) =>
        new()
        {
            DocumentPath = path,
            Range = new SourceRange(0, 0, 0, 3),
            SymbolId = "symbol",
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };

    private static Uri FileUri(string path) => new(Path.GetFullPath(path), UriKind.Absolute);
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "live-semantic-tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeLiveNavigation(LiveSemanticNavigationResult result)
        : ILiveSemanticNavigation
    {
        public Task<LiveSemanticNavigationResult> GoToDefinitionAsync(
            string workspaceRoot, string documentPath, int line, int utf16Column,
            CancellationToken cancellationToken) => Task.FromResult(result);

        public Task<LiveSemanticNavigationResult> FindReferencesAsync(
            string workspaceRoot, string documentPath, int line, int utf16Column,
            bool includeDefinition, CancellationToken cancellationToken) => Task.FromResult(result);

        public Task<LiveSemanticNavigationResult> FindImplementationsAsync(
            string workspaceRoot, string documentPath, int line, int utf16Column,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeClient : ILanguageServerClient
    {
        public IReadOnlyList<LspLocation> Definitions { get; init; } = [];
        public Task InitializeAsync(string workspaceRoot, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task DidOpenAsync(LspTextDocument document, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task DidChangeAsync(LspTextDocument document, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task DidCloseAsync(Uri documentUri, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
            Uri documentUri, int line, int utf16Column, CancellationToken cancellationToken) =>
            Task.FromResult(Definitions);
        public Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
            Uri documentUri, int line, int utf16Column, bool includeDefinition,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LspLocation>>([]);
        public Task<IReadOnlyList<LspLocation>> FindImplementationsAsync(
            Uri documentUri, int line, int utf16Column,
            CancellationToken cancellationToken) => Task.FromResult(Definitions);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
