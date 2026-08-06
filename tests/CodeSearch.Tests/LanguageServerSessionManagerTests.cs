using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public class LanguageServerSessionManagerTests
{
    [Fact]
    public async Task ReusesASessionAndSendsMonotonicOpenAndChangeVersions()
    {
        var clients = new List<FakeClient>();
        await using var manager = new LanguageServerSessionManager((_, _) =>
        {
            var client = new FakeClient();
            clients.Add(client);
            return client;
        });
        var root = TempRoot();

        await manager.OpenOrUpdateAsync(root, "src/a.ts", "typescript", 1, "const a = 1;", Ct);
        await manager.OpenOrUpdateAsync(root, "src/a.ts", "typescript", 2, "const a = 2;", Ct);
        await manager.OpenOrUpdateAsync(root, "src/b.ts", "typescript", 1, "a;", Ct);

        var client = Assert.Single(clients);
        Assert.Equal(1, client.InitializeCount);
        Assert.Equal([1, 1], client.Opened.Select(document => document.Version));
        Assert.Equal([2], client.Changed.Select(document => document.Version));
    }

    [Fact]
    public async Task RejectsStaleDocumentVersionsWithoutNotifyingTheServer()
    {
        var client = new FakeClient();
        await using var manager = new LanguageServerSessionManager((_, _) => client);
        var root = TempRoot();
        await manager.OpenOrUpdateAsync(root, "a.py", "python", 4, "value = 1", Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.OpenOrUpdateAsync(root, "a.py", "python", 4, "value = 2", Ct));

        Assert.Empty(client.Changed);
    }

    [Fact]
    public async Task RoutesNavigationQueriesForOpenDocumentsOnly()
    {
        var client = new FakeClient
        {
            Definitions = [new LspLocation(new Uri("file:///definition.ts"), new SourceRange(1, 2, 1, 5))],
            References = [new LspLocation(new Uri("file:///reference.ts"), new SourceRange(3, 4, 3, 7))],
            Implementations = [new LspLocation(new Uri("file:///implementation.ts"), new SourceRange(5, 6, 5, 9))],
        };
        await using var manager = new LanguageServerSessionManager((_, _) => client);
        var root = TempRoot();
        await manager.OpenOrUpdateAsync(root, "a.ts", "typescript", 1, "run();", Ct);

        var definitions = await manager.GoToDefinitionAsync(root, "a.ts", 0, 1, Ct);
        var references = await manager.FindReferencesAsync(root, "a.ts", 0, 1, true, Ct);
        var implementations = await manager.FindImplementationsAsync(root, "a.ts", 0, 1, Ct);
        var unopened = await manager.GoToDefinitionAsync(root, "closed.ts", 0, 1, Ct);

        Assert.Equal(client.Definitions, definitions);
        Assert.Equal(client.References, references);
        Assert.Equal(client.Implementations, implementations);
        Assert.Empty(unopened);
        Assert.True(client.LastIncludeDefinition);
    }

    [Fact]
    public async Task LanguageChangeClosesTheOldSessionBeforeOpeningTheNewOne()
    {
        var clients = new Dictionary<string, FakeClient>(StringComparer.Ordinal);
        await using var manager = new LanguageServerSessionManager((_, language) =>
            clients[language] = new FakeClient());
        var root = TempRoot();
        await manager.OpenOrUpdateAsync(root, "view.txt", "plaintext", 1, "first", Ct);

        await manager.OpenOrUpdateAsync(root, "view.txt", "html", 1, "<p>second</p>", Ct);

        Assert.Single(clients["plaintext"].Closed);
        Assert.Single(clients["html"].Opened);
    }

    [Fact]
    public async Task CloseAndDisposeNotifyTheOwningServers()
    {
        var client = new FakeClient();
        var manager = new LanguageServerSessionManager((_, _) => client);
        var root = TempRoot();
        await manager.OpenOrUpdateAsync(root, "a.cs", "csharp", 1, "class A {}", Ct);
        await manager.OpenOrUpdateAsync(root, "b.cs", "csharp", 1, "class B {}", Ct);

        await manager.CloseAsync(root, "a.cs", Ct);
        await manager.DisposeAsync();

        Assert.Equal(2, client.Closed.Count);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task RejectsDocumentsOutsideTheWorkspace()
    {
        await using var manager = new LanguageServerSessionManager((_, _) => new FakeClient());
        var root = TempRoot();

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.OpenOrUpdateAsync(root, "../outside.ts", "typescript", 1, "bad", Ct));
    }

    [Fact]
    public async Task RestartsACrashedServerAndReplaysOpenDocumentsBeforeRetrying()
    {
        var crashed = new FakeClient { DefinitionFailure = new EndOfStreamException("crashed") };
        var recovered = new FakeClient
        {
            Definitions =
            [
                new LspLocation(
                    new Uri("file:///recovered.ts"),
                    new SourceRange(1, 0, 1, 3)),
            ],
        };
        var clients = new Queue<FakeClient>([crashed, recovered]);
        await using var manager = new LanguageServerSessionManager((_, _) => clients.Dequeue());
        var root = TempRoot();
        await manager.OpenOrUpdateAsync(root, "a.ts", "typescript", 7, "run();", Ct);

        var definitions = await manager.GoToDefinitionAsync(root, "a.ts", 0, 1, Ct);

        Assert.Single(definitions);
        Assert.True(crashed.Disposed);
        Assert.Equal(1, recovered.InitializeCount);
        var replayed = Assert.Single(recovered.Opened);
        Assert.Equal(7, replayed.Version);
        Assert.Equal("run();", replayed.Text);
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "lsp-session-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeClient : ILanguageServerClient
    {
        public int InitializeCount { get; private set; }
        public List<LspTextDocument> Opened { get; } = [];
        public List<LspTextDocument> Changed { get; } = [];
        public List<Uri> Closed { get; } = [];
        public IReadOnlyList<LspLocation> Definitions { get; init; } = [];
        public IReadOnlyList<LspLocation> References { get; init; } = [];
        public IReadOnlyList<LspLocation> Implementations { get; init; } = [];
        public bool LastIncludeDefinition { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? DefinitionFailure { get; init; }

        public Task InitializeAsync(string workspaceRoot, CancellationToken cancellationToken)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task DidOpenAsync(LspTextDocument document, CancellationToken cancellationToken)
        {
            Opened.Add(document);
            return Task.CompletedTask;
        }

        public Task DidChangeAsync(LspTextDocument document, CancellationToken cancellationToken)
        {
            Changed.Add(document);
            return Task.CompletedTask;
        }

        public Task DidCloseAsync(Uri documentUri, CancellationToken cancellationToken)
        {
            Closed.Add(documentUri);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
            Uri documentUri,
            int line,
            int utf16Column,
            CancellationToken cancellationToken) =>
            DefinitionFailure is not null
                ? Task.FromException<IReadOnlyList<LspLocation>>(DefinitionFailure)
                : Task.FromResult(Definitions);

        public Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
            Uri documentUri,
            int line,
            int utf16Column,
            bool includeDefinition,
            CancellationToken cancellationToken)
        {
            LastIncludeDefinition = includeDefinition;
            return Task.FromResult(References);
        }

        public Task<IReadOnlyList<LspLocation>> FindImplementationsAsync(
            Uri documentUri,
            int line,
            int utf16Column,
            CancellationToken cancellationToken) =>
            Task.FromResult(Implementations);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
