using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class LspLanguageFixtureIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-real-lsp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TypeScriptLanguageServerProvidesExactCrossFileNavigation()
    {
        var executable = Environment.GetEnvironmentVariable(
            "LOCALAI_LSP_TYPESCRIPT_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            Assert.Skip("Set LOCALAI_LSP_TYPESCRIPT_EXECUTABLE to run the real fixture.");
        }

        Write("tsconfig.json", """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "CommonJS",
                "strict": true
              },
              "include": ["src/**/*.ts"]
            }
            """);
        var definitionText = Write("src/definition.ts", """
            export function greet(name: string): string {
              return `Hello, ${name}`;
            }
            """);
        var usageText = Write("src/usage.ts", """
            import { greet } from "./definition";

            export const message = greet("LocalAi");
            """);

        await using var manager = Manager(executable, ["--stdio"]);
        await manager.OpenOrUpdateAsync(
            _root, "src/definition.ts", "typescript", 1, definitionText, Ct);
        await manager.OpenOrUpdateAsync(
            _root, "src/usage.ts", "typescript", 1, usageText, Ct);

        var definitions = await manager.GoToDefinitionAsync(
            _root, "src/usage.ts", 2, 25, Ct);
        var references = await manager.FindReferencesAsync(
            _root, "src/usage.ts", 2, 25, includeDefinition: true, Ct);

        Assert.Contains(definitions, location =>
            Relative(location.Uri) == "src/definition.ts" &&
            location.Range == new SourceRange(0, 16, 0, 21));
        Assert.Contains(references, location =>
            Relative(location.Uri) == "src/usage.ts" &&
            location.Range == new SourceRange(2, 23, 2, 28));
    }

    [Fact]
    public async Task PyrightLanguageServerProvidesExactCrossFileNavigation()
    {
        var executable = Environment.GetEnvironmentVariable("LOCALAI_LSP_PYRIGHT_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            Assert.Skip("Set LOCALAI_LSP_PYRIGHT_EXECUTABLE to run the real fixture.");
        }

        var definitionText = Write("definition.py", """
            def greet(name: str) -> str:
                return f"Hello, {name}"
            """);
        var usageText = Write("usage.py", """
            from definition import greet

            message = greet("LocalAi")
            """);

        await using var manager = Manager(executable, ["--stdio"]);
        await manager.OpenOrUpdateAsync(
            _root, "definition.py", "python", 1, definitionText, Ct);
        await manager.OpenOrUpdateAsync(
            _root, "usage.py", "python", 1, usageText, Ct);

        var definitions = await manager.GoToDefinitionAsync(
            _root, "usage.py", 2, 12, Ct);
        var references = await manager.FindReferencesAsync(
            _root, "usage.py", 2, 12, includeDefinition: true, Ct);

        Assert.Contains(definitions, location =>
            Relative(location.Uri) == "definition.py" &&
            location.Range == new SourceRange(0, 4, 0, 9));
        Assert.Contains(references, location =>
            Relative(location.Uri) == "usage.py" &&
            location.Range == new SourceRange(2, 10, 2, 15));
    }

    private LanguageServerSessionManager Manager(
        string executable,
        IReadOnlyList<string> arguments) =>
        new((workspaceRoot, _) => StdioLanguageServerClient.Start(
            workspaceRoot,
            new LanguageServerProcessSpec(
                executable,
                arguments,
                RequestTimeout: TimeSpan.FromSeconds(30),
                ShutdownTimeout: TimeSpan.FromSeconds(5),
                MaximumMessageBytes: 16 * 1024 * 1024)));

    private string Write(string relativePath, string text)
    {
        Directory.CreateDirectory(_root);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, normalized);
        return normalized;
    }

    private string Relative(Uri uri) =>
        Path.GetRelativePath(_root, uri.LocalPath).Replace('\\', '/');

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
