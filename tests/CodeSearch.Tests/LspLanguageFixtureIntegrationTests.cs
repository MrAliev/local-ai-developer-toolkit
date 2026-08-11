using System.Text.Json;
using CodeSearch.Core.Semantics;
using LocalAi.TestSupport;

namespace CodeSearch.Tests;

public sealed class LspLanguageFixtureIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-real-lsp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TypeScriptLanguageServerProvidesExactCrossFileNavigation()
    {
        // Discovered the way the product discovers it, so an installed server runs the fixture
        // instead of an environment variable being the only way in.
        var executable = FixturePrerequisite.RequireText(
            Environment.GetEnvironmentVariable("LOCALAI_LSP_TYPESCRIPT_EXECUTABLE")
                ?? ExecutableResolver.Find("typescript-language-server"),
            "typescript-language-server",
            "Install it, or set LOCALAI_LSP_TYPESCRIPT_EXECUTABLE.");

        // The second prerequisite, and the one whose absence used to look like a product failure.
        // The server does not carry a TypeScript: it looks for one in the workspace, and this
        // fixture's workspace is a temporary directory with no node_modules. Naming a tsserver
        // explicitly is both what makes the fixture hermetic and the feature under test.
        var tsserver = FixturePrerequisite.RequireText(
            FindTsServer(executable),
            "a usable tsserver.js beside typescript-language-server",
            "Install TypeScript 5.x (7.x no longer ships lib/tsserver.js), or set " +
            "LOCALAI_LSP_TYPESCRIPT_TSSERVER_PATH.");

        // Past this point both prerequisites are present, so anything that goes wrong is a
        // genuine failure and the test says so instead of skipping. Skipping on a failed
        // initialize is what let the 0.1.31 and 0.1.32 defects ship unseen.
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

        await using var manager = Manager(
            executable,
            ["--stdio"],
            JsonSerializer.SerializeToElement(new { tsserver = new { path = tsserver } }));
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
        var executable = FixturePrerequisite.RequireText(
            Environment.GetEnvironmentVariable("LOCALAI_LSP_PYRIGHT_EXECUTABLE")
                ?? ExecutableResolver.Find("pyright-langserver"),
            "pyright-langserver",
            "Install pyright, or set LOCALAI_LSP_PYRIGHT_EXECUTABLE.");

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
        IReadOnlyList<string> arguments,
        JsonElement? initializationOptions = null) =>
        new((workspaceRoot, _) => StdioLanguageServerClient.Start(
            workspaceRoot,
            new LanguageServerProcessSpec(
                executable,
                arguments,
                RequestTimeout: TimeSpan.FromSeconds(30),
                ShutdownTimeout: TimeSpan.FromSeconds(5),
                MaximumMessageBytes: 16 * 1024 * 1024,
                InitializationOptions: initializationOptions)));

    /// <summary>
    /// Finds a tsserver the language server can actually run, or null when there is none.
    ///
    /// Presence of <c>lib/tsserver.js</c> is the whole test of usability, and it is not a
    /// formality: TypeScript 7 stopped shipping that file, so a machine with a perfectly current
    /// global TypeScript still has nothing typescript-language-server 5.x can drive. Searching
    /// beside the server rather than on PATH is deliberate — a TypeScript installed as some other
    /// package's dependency is still a working one.
    /// </summary>
    private static string? FindTsServer(string serverExecutable)
    {
        var configured = Environment.GetEnvironmentVariable(
            "LOCALAI_LSP_TYPESCRIPT_TSSERVER_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // An explicitly named path that does not exist is a mistake worth surfacing as a
            // skip reason rather than silently searching somewhere else instead.
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var installRoot = Path.GetDirectoryName(Path.GetFullPath(serverExecutable));
        if (installRoot is null)
        {
            return null;
        }

        var modules = Path.Combine(installRoot, "node_modules");
        return Directory.Exists(modules)
            ? TypeScriptPackages(modules)
                .Select(package => Path.Combine(package, "lib", "tsserver.js"))
                .FirstOrDefault(File.Exists)
            : null;
    }

    private static IEnumerable<string> TypeScriptPackages(string modules)
    {
        yield return Path.Combine(modules, "typescript");
        foreach (var entry in Directories(modules))
        {
            yield return Path.Combine(entry, "node_modules", "typescript");
            if (!Path.GetFileName(entry).StartsWith('@'))
            {
                continue;
            }

            foreach (var scoped in Directories(entry))
            {
                yield return Path.Combine(scoped, "node_modules", "typescript");
            }
        }
    }

    private static IEnumerable<string> Directories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

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
