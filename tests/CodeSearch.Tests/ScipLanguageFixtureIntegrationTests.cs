using CodeSearch.Core.Semantics;

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
        var installedExecutable = Environment.GetEnvironmentVariable(
            "LOCALAI_SCIP_TYPESCRIPT_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(installedExecutable) &&
            (string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(script)))
        {
            Assert.Skip(
                "Set LOCALAI_SCIP_NODE and LOCALAI_SCIP_TYPESCRIPT_SCRIPT to run the real fixture.");
        }

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
        var installedExecutable = Environment.GetEnvironmentVariable(
            "LOCALAI_SCIP_PYTHON_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(installedExecutable) && OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "scip-python.cmd");
            if (File.Exists(candidate))
            {
                installedExecutable = candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(installedExecutable) &&
            (string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(script)))
        {
            Assert.Skip(
                "Install scip-python or set LOCALAI_SCIP_NODE and " +
                "LOCALAI_SCIP_PYTHON_SCRIPT to run the real fixture.");
        }

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
