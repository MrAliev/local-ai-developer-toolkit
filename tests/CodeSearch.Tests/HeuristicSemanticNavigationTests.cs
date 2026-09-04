using CodeSearch.Core.Resources;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class HeuristicSemanticNavigationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "heuristic-navigation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResolvesHtmlIdsButNeverClaimsPreciseNavigation()
    {
        Write("index.html", """
            <label for="user-name">User</label>
            <input id="user-name">
            <script>document.getElementById("user-name")</script>
            """);
        var navigation = Navigation();

        var definitions = await navigation.GoToDefinitionAsync(
            _root, "index.html", 0, 13, Ct);
        var references = await navigation.FindReferencesAsync(
            _root, "index.html", 0, 13, includeDefinition: false, Ct);

        var definition = Assert.Single(definitions);
        Assert.Equal(new SourceRange(1, 11, 1, 20), definition.Range);
        Assert.Equal(SemanticOccurrenceRoles.Definition, definition.Roles);
        Assert.Equal(NavigationPrecision.Heuristic, definition.Precision);
        Assert.Equal(2, references.Count);
        Assert.All(references, location =>
            Assert.Equal(NavigationPrecision.Heuristic, location.Precision));
    }

    [Fact]
    public async Task ResolvesACssClassFromAnHtmlReference()
    {
        Write("styles.css", ".primary-button { color: red; }");
        Write("index.html", "<button class=\"primary-button\">Run</button>");

        var definitions = await Navigation().GoToDefinitionAsync(
            _root, "index.html", 0, 16, Ct);

        var definition = Assert.Single(definitions);
        Assert.Equal("styles.css", definition.DocumentPath);
        Assert.Equal(new SourceRange(0, 1, 0, 15), definition.Range);
        Assert.Equal(NavigationPrecision.Heuristic, definition.Precision);
    }

    [Fact]
    public async Task HonorsTokenBoundariesAndTheConfiguredResultLimit()
    {
        Write("source.js", "function run() {}\nrun();\nrunning();\nrun();");
        var store = Store();
        store.Write(HeuristicNavigationPolicy.Default with { MaximumResults = 2 });

        var references = await new HeuristicSemanticNavigation(store).FindReferencesAsync(
            _root, "source.js", 1, 1, includeDefinition: true, Ct);

        Assert.Equal(2, references.Count);
        Assert.DoesNotContain(references, location => location.Range.StartLine == 2);
    }

    [Fact]
    public async Task CanBeDisabledWithoutRebuildingCode()
    {
        Write("source.js", "function run() {}\nrun();");
        var store = Store();
        store.Write(HeuristicNavigationPolicy.Default with { Enabled = false });

        var results = await new HeuristicSemanticNavigation(store).FindReferencesAsync(
            _root, "source.js", 1, 1, includeDefinition: true, Ct);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("settings.JSON", "{ \"service-name\": true }")]
    [InlineData("settings.YAML", "service-name: true")]
    public async Task RecognizesConfigurationKeysCaseInsensitively(
        string path,
        string contents)
    {
        Write(path, contents);

        var definitions = await Navigation().GoToDefinitionAsync(
            _root, path, 0, 4, Ct);

        var definition = Assert.Single(definitions);
        Assert.Equal(path, definition.DocumentPath);
        Assert.Equal(SemanticOccurrenceRoles.Definition, definition.Roles);
        Assert.Equal(NavigationPrecision.Heuristic, definition.Precision);
    }

    [Fact]
    public void InvalidPolicyFallsBackToSafeDefaults()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, HeuristicNavigationPolicy.FileName),
            "{ \"schemaVersion\": 1, \"enabled\": true, \"maximumFiles\": 0 }");

        Assert.Equal(HeuristicNavigationPolicy.Default, Store().Read());
    }

    [Fact]
    public async Task GatewayUsesSidxBeforeHeuristicsAndFallsBackOnlyOnAnEmptyExactResult()
    {
        var heuristicLocation = new SemanticLocation(
            "fallback.html",
            new SourceRange(0, 0, 0, 3),
            "heuristic",
            SemanticOccurrenceRoles.Definition,
            NavigationPrecision.Heuristic);
        var heuristic = new FakeHeuristic([heuristicLocation]);
        var preciseGateway = Gateway(heuristic, withOccurrence: true);

        var precise = await preciseGateway.GoToDefinitionAsync("Use.cs", 0, 1, _root, Ct);

        Assert.Equal(NavigationPrecision.Precise, Assert.Single(precise).Precision);
        Assert.Equal(0, heuristic.DefinitionCalls);

        var fallbackGateway = Gateway(heuristic, withOccurrence: false);
        var fallback = await fallbackGateway.GoToDefinitionAsync("Use.cs", 0, 1, _root, Ct);

        Assert.Equal(NavigationPrecision.Heuristic, Assert.Single(fallback).Precision);
        Assert.Equal(1, heuristic.DefinitionCalls);
    }

    [Fact]
    public async Task A_precise_answer_carries_no_degradation()
    {
        var gateway = Gateway(new FakeHeuristic([]), withOccurrence: true);

        var outcome = await gateway.ResolveDefinitionAsync("Use.cs", 0, 1, _root, Ct);

        Assert.Null(outcome.Degradation);
        Assert.Equal(NavigationPrecision.Precise, Assert.Single(outcome.Locations).Precision);
    }

    [Fact]
    public async Task A_generation_without_a_semantic_index_says_so_instead_of_only_falling_back()
    {
        // This is the state every repository that has not been re-synced since semantic indexing
        // shipped is in. The gateway already built the explanation and then threw it away to
        // reach the text fallback, so the caller saw README and docs matches and no reason.
        var heuristic = new FakeHeuristic([HeuristicLocation]);
        var gateway = new SemanticNavigationGateway(
            // The production sentence rather than a copy of it: a fixture that quotes a
            // message the product no longer prints reads like a promise about the product.
            _ => throw new SemanticNavigationNotReadyException(
                IndexText.GenerationWithoutSemanticIndex("ff114b066164", @"R:epo")),
            heuristicNavigation: heuristic);

        var outcome = await gateway.ResolveReferencesAsync("Use.cs", 0, 1, true, _root, Ct);

        Assert.Equal(NavigationPrecision.Heuristic, Assert.Single(outcome.Locations).Precision);
        Assert.NotNull(outcome.Degradation);
        Assert.Contains("has no semantic.sidx", outcome.Degradation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_index_with_nothing_at_the_position_is_reported_as_its_own_reason()
    {
        var gateway = Gateway(new FakeHeuristic([HeuristicLocation]), withOccurrence: false);

        var outcome = await gateway.ResolveDefinitionAsync("Use.cs", 0, 1, _root, Ct);

        // Only one of the two reasons is fixed by re-syncing, so they must not read alike.
        Assert.NotNull(outcome.Degradation);
        Assert.Contains(
            "no symbol at this position",
            outcome.Degradation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("semantic.sidx", outcome.Degradation, StringComparison.Ordinal);
    }

    private static SemanticLocation HeuristicLocation => new(
        "README.md",
        new SourceRange(0, 0, 0, 3),
        "heuristic",
        SemanticOccurrenceRoles.Reference,
        NavigationPrecision.Heuristic);

    private SemanticNavigationGateway Gateway(FakeHeuristic heuristic, bool withOccurrence)
    {
        var index = new SemanticIndex
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [Document("Def.cs", 1), Document("Use.cs", 2)],
            Symbols =
            [
                new SemanticSymbol
                {
                    Id = "precise",
                    DisplayName = "Run",
                    Kind = SemanticSymbolKind.Method,
                },
            ],
            Occurrences = withOccurrence
                ?
                [
                    Occurrence("Def.cs", SemanticOccurrenceRoles.Definition),
                    Occurrence("Use.cs", SemanticOccurrenceRoles.Reference),
                ]
                : [],
            Relationships = [],
        };
        var snapshot = new SemanticSnapshotIdentity("repository", "generation", "tree", null);
        return new SemanticNavigationGateway(
            _ => new SemanticNavigationContext(new SemanticNavigationService(index), snapshot),
            heuristicNavigation: heuristic);
    }

    private HeuristicSemanticNavigation Navigation() => new(Store());
    private HeuristicNavigationPolicyStore Store() => new(_root);

    private void Write(string relativePath, string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static SemanticDocument Document(string path, byte value) =>
        new() { RelPath = path, Hash = Enumerable.Repeat(value, 32).ToArray() };

    private static SemanticOccurrence Occurrence(string path, SemanticOccurrenceRoles roles) =>
        new()
        {
            DocumentPath = path,
            Range = new SourceRange(0, 0, 0, 3),
            SymbolId = "precise",
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeHeuristic(IReadOnlyList<SemanticLocation> locations)
        : IHeuristicSemanticNavigation
    {
        public int DefinitionCalls { get; private set; }

        public Task<IReadOnlyList<SemanticLocation>> GoToDefinitionAsync(
            string repositoryRoot, string documentPath, int line, int utf16Column,
            CancellationToken cancellationToken)
        {
            DefinitionCalls++;
            return Task.FromResult(locations);
        }

        public Task<IReadOnlyList<SemanticLocation>> FindReferencesAsync(
            string repositoryRoot, string documentPath, int line, int utf16Column,
            bool includeDefinition, CancellationToken cancellationToken) =>
            Task.FromResult(locations);
    }
}
