using CodeSearch.Core.Semantics;

using LocalAi.TestSupport;

namespace CodeSearch.Tests;

public sealed class ScipLanguageFixtureIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-real-scip-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScipTypeScriptProvidesExactCrossFileNavigation()
    {
        var node = Environment.GetEnvironmentVariable("LOCALAI_SCIP_NODE");
        var script = Environment.GetEnvironmentVariable("LOCALAI_SCIP_TYPESCRIPT_SCRIPT");
        // Found the way the product finds it. Requiring an environment variable for a tool that
        // is installed and on the PATH meant this skipped on every machine that could have run
        // it — which is how the TypeScript path went unexercised while scip-typescript sat in
        // %APPDATA%\npm.
        var installedExecutable = Environment.GetEnvironmentVariable(
            "LOCALAI_SCIP_TYPESCRIPT_EXECUTABLE")
            ?? ExecutableResolver.Find("scip-typescript");
        FixturePrerequisite.Require(
            !string.IsNullOrWhiteSpace(installedExecutable) ||
                (!string.IsNullOrWhiteSpace(node) && !string.IsNullOrWhiteSpace(script)),
            "scip-typescript",
            "Install it, or set LOCALAI_SCIP_NODE and LOCALAI_SCIP_TYPESCRIPT_SCRIPT.");

        Write("tsconfig.json", """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "CommonJS",
                "strict": true,
                "skipLibCheck": true
              },
              "include": ["src/**/*.ts"]
            }
            """);
        Write("src/definition.ts", """
            export function greet(name: string): string {
              return `Hello, ${name}`;
            }
            """);
        Write("src/usage.ts", """
            import { greet } from "./definition";

            export const message = greet("LocalAi");
            """);

        var result = await RunAsync(
            "typescript",
            installedExecutable ?? node!,
            installedExecutable is null ? [script!, "index"] : ["index"],
            ScipPositionEncoding.Utf16,
            TestContext.Current.CancellationToken);

        Assert.True(
            result.Status.State == SemanticAdapterState.Succeeded,
            result.Status.Message);
        var navigation = new SemanticNavigationService(result.Index);
        var definition = Assert.Single(navigation.GoToDefinition(
            "src/usage.ts", 2, 25, Snapshot()));
        var references = navigation.FindReferences(
            "src/usage.ts", 2, 25, includeDefinition: false, Snapshot());
        Assert.Equal("src/definition.ts", definition.DocumentPath);
        Assert.Equal(new SourceRange(0, 16, 0, 21), definition.Range);
        Assert.Contains(references, location =>
            location.DocumentPath == "src/usage.ts" &&
            location.Range == new SourceRange(2, 23, 2, 28));
    }

    [Fact]
    public async Task ScipPythonProvidesExactCrossFileNavigation()
    {
        var node = Environment.GetEnvironmentVariable("LOCALAI_SCIP_NODE");
        var script = Environment.GetEnvironmentVariable("LOCALAI_SCIP_PYTHON_SCRIPT");
        // The same discovery the product uses, rather than one hard-coded npm path: this looked
        // only in %APPDATA%\npm for a .cmd, so any other install location skipped silently.
        var installedExecutable = Environment.GetEnvironmentVariable(
            "LOCALAI_SCIP_PYTHON_EXECUTABLE")
            ?? ExecutableResolver.Find("scip-python");

        FixturePrerequisite.Require(
            !string.IsNullOrWhiteSpace(installedExecutable) ||
                (!string.IsNullOrWhiteSpace(node) && !string.IsNullOrWhiteSpace(script)),
            "scip-python",
            "Install it, or set LOCALAI_SCIP_NODE and LOCALAI_SCIP_PYTHON_SCRIPT.");

        Write("definition.py", """
            def greet(name: str) -> str:
                return f"Hello, {name}"
            """);
        Write("usage.py", """
            from definition import greet

            message = greet("LocalAi")
            """);

        var result = await RunAsync(
            "python",
            installedExecutable ?? node!,
            installedExecutable is null
                ? [script!, "index", ".", "--project-name", "fixture", "--project-version", "1.0"]
                : ["index", ".", "--project-name", "fixture", "--project-version", "1.0"],
            ScipPositionEncoding.Utf32,
            TestContext.Current.CancellationToken);

        Assert.True(
            result.Status.State == SemanticAdapterState.Succeeded,
            result.Status.Message);
        var navigation = new SemanticNavigationService(result.Index);
        var definition = Assert.Single(navigation.GoToDefinition(
            "usage.py", 2, 12, Snapshot()));
        var references = navigation.FindReferences(
            "usage.py", 2, 12, includeDefinition: false, Snapshot());
        Assert.Equal("definition.py", definition.DocumentPath);
        Assert.Equal(new SourceRange(0, 4, 0, 9), definition.Range);
        Assert.Contains(references, location =>
            location.DocumentPath == "usage.py" &&
            location.Range == new SourceRange(2, 10, 2, 15));
    }

    [Fact]
    public async Task ScipPythonReportsTheBodyOfEveryDefinition()
    {
        var node = Environment.GetEnvironmentVariable("LOCALAI_SCIP_NODE");
        var script = Environment.GetEnvironmentVariable("LOCALAI_SCIP_PYTHON_SCRIPT");
        var installedExecutable = Environment.GetEnvironmentVariable(
            "LOCALAI_SCIP_PYTHON_EXECUTABLE")
            ?? ExecutableResolver.Find("scip-python");
        FixturePrerequisite.Require(
            !string.IsNullOrWhiteSpace(installedExecutable) ||
                (!string.IsNullOrWhiteSpace(node) && !string.IsNullOrWhiteSpace(script)),
            "scip-python",
            "Install it, or set LOCALAI_SCIP_NODE and LOCALAI_SCIP_PYTHON_SCRIPT.");

        Write("shapes.py", """
            def apply_tax(amount: float) -> float:
                return amount * 1.2


            class Invoice:
                def total(self) -> float:
                    return 0.0
            """);

        var result = await RunAsync(
            "python",
            installedExecutable ?? node!,
            installedExecutable is null
                ? [script!, "index", ".", "--project-name", "fixture", "--project-version", "1.0"]
                : ["index", ".", "--project-name", "fixture", "--project-version", "1.0"],
            ScipPositionEncoding.Utf32,
            TestContext.Current.CancellationToken);

        Assert.True(
            result.Status.State == SemanticAdapterState.Succeeded,
            result.Status.Message);

        // The body of `apply_tax` is its two lines, not the eleven characters of its name.
        var function = DefinitionNamed(result.Index, "shapes.py", "apply_tax");
        Assert.Equal(new SourceRange(0, 0, 1, 23), function.EnclosingRange);

        // A method inside a class gets one too, and it ends where the method ends rather than
        // where the class does.
        var method = DefinitionNamed(result.Index, "shapes.py", "total");
        Assert.NotNull(method.EnclosingRange);
        Assert.Equal(5, method.EnclosingRange!.Value.StartLine);
        Assert.Equal(6, method.EnclosingRange!.Value.EndLine);
    }

    [Fact]
    public async Task ScipTypeScriptReportsBodiesForNamedDefinitionsAndNotForNestedOnes()
    {
        var node = Environment.GetEnvironmentVariable("LOCALAI_SCIP_NODE");
        var script = Environment.GetEnvironmentVariable("LOCALAI_SCIP_TYPESCRIPT_SCRIPT");
        var installedExecutable = Environment.GetEnvironmentVariable(
            "LOCALAI_SCIP_TYPESCRIPT_EXECUTABLE")
            ?? ExecutableResolver.Find("scip-typescript");
        FixturePrerequisite.Require(
            !string.IsNullOrWhiteSpace(installedExecutable) ||
                (!string.IsNullOrWhiteSpace(node) && !string.IsNullOrWhiteSpace(script)),
            "scip-typescript",
            "Install it, or set LOCALAI_SCIP_NODE and LOCALAI_SCIP_TYPESCRIPT_SCRIPT.");

        Write("tsconfig.json", """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "CommonJS",
                "strict": true,
                "skipLibCheck": true
              },
              "include": ["src/**/*.ts"]
            }
            """);
        Write("src/shapes.ts", """
            export function outer(value: number): number {
              function inner(x: number): number {
                return x + 1;
              }

              return inner(value);
            }
            """);

        var result = await RunAsync(
            "typescript",
            installedExecutable ?? node!,
            installedExecutable is null ? [script!, "index"] : ["index"],
            ScipPositionEncoding.Utf16,
            TestContext.Current.CancellationToken);

        Assert.True(
            result.Status.State == SemanticAdapterState.Succeeded,
            result.Status.Message);

        // A definition with a global symbol carries its body: the whole function, brace to brace.
        var outer = DefinitionNamed(result.Index, "src/shapes.ts", "outer");
        Assert.Equal(new SourceRange(0, 0, 6, 1), outer.EnclosingRange);

        // One declared inside a function body does not, because scip-typescript makes it a
        // `local` and never reports a body span for those. Measured in issue #87 and encoded
        // here so that a future adapter upgrade changing it shows up as a failing test rather
        // than as silently different chunk boundaries.
        var inner = DefinitionNamed(result.Index, "src/shapes.ts", "inner");
        Assert.StartsWith("scip-local", inner.SymbolId, StringComparison.Ordinal);
        Assert.Null(inner.EnclosingRange);
    }

    /// <summary>
    /// The definition whose name range spells <paramref name="name"/> in the source.
    /// </summary>
    /// <remarks>
    /// Looked up through the text rather than through the symbol id or a display name, because
    /// neither is dependable across the two indexers: scip-python composes ids like
    /// `shapes/Invoice#total().` and leaves the display name empty, and a definition nested in a
    /// function body has no name in its id at all — it is `local 3`. The name range always spells
    /// the identifier, whatever the indexer called the symbol.
    /// </remarks>
    private SemanticOccurrence DefinitionNamed(
        SemanticIndex index,
        string documentPath,
        string name)
    {
        var lines = File.ReadAllText(Path.Combine(_root, documentPath))
            .Replace("\r\n", "\n")
            .Split('\n');
        var candidates = index.Occurrences
            .Where(occurrence =>
                occurrence.DocumentPath == documentPath &&
                occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition))
            .ToArray();
        var matches = candidates
            .Where(occurrence => Spelling(lines, occurrence.Range) == name)
            .ToArray();
        Assert.True(
            matches.Length == 1,
            $"Expected one definition spelled '{name}' in '{documentPath}', found " +
            $"{matches.Length}. Definitions: " +
            string.Join(", ", candidates.Select(occurrence =>
                $"{Spelling(lines, occurrence.Range)}={occurrence.SymbolId}")));
        return matches[0];
    }

    private static string Spelling(string[] lines, SourceRange range)
    {
        if (range.StartLine != range.EndLine || range.StartLine >= lines.Length)
        {
            return string.Empty;
        }

        var line = lines[range.StartLine];
        var end = Math.Min(range.EndCharacter, line.Length);
        return range.StartCharacter >= end
            ? string.Empty
            : line[range.StartCharacter..end];
    }

    private async Task<ScipAdapterRunResult> RunAsync(
        string name,
        string executable,
        IReadOnlyList<string> arguments,
        ScipPositionEncoding fallbackEncoding,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        return await new ScipAdapterRunner().RunAsync(
            EmptyIndex(),
            _root,
            new ScipAdapterSpec(
                name,
                executable,
                arguments,
                Timeout: TimeSpan.FromMinutes(2),
                MaximumOutputBytes: 1024 * 1024,
                UnspecifiedPositionEncoding: fallbackEncoding),
            cancellationToken);
    }

    private void Write(string relativePath, string text)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static SemanticSnapshotIdentity Snapshot() =>
        new("repository", "generation", "tree", null);

    private static SemanticIndex EmptyIndex() =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            BaseCommit = "commit",
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
