using CodeSearch.Core.Semantics;
using LocalAi.Cli;
using LocalAi.TestSupport;

namespace LocalAi.IntegrationTests;

public sealed class SyntheticTypeScriptProjectIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"localai-synthetic-typescript-{Guid.NewGuid():N}");

    [Fact]
    public async Task Installed_scip_typescript_indexes_javascript_with_stable_root_relative_symbols()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows npm shim behavior is Windows-specific.");
        }

        // Resolved rather than assembled from the npm folder under %APPDATA%. npm's global
        // prefix is configurable and is not that path everywhere — the CI runner puts it under
        // C:/npm/prefix — so the hardcoded form reported the tool missing on a machine that had
        // it installed.
        var executable = FixturePrerequisite.RequireText(
            CodeSearch.Core.Semantics.ExecutableResolver.Find("scip-typescript"),
            "@sourcegraph/scip-typescript",
            "Install it with npm so the npm shim this test exercises exists.");

        Directory.CreateDirectory(root);
        const string relative = "src/app.js";
        var source = Path.Combine(root, "src", "app.js");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(
            source,
            "export function greet(name) { return `Hello ${name}`; }\n" +
            "export const message = greet('LocalAi');\n",
            TestContext.Current.CancellationToken);
        var workspace = CodeSearchSyncCommand.CreateSyntheticTypeScriptWorkspace(
            root,
            [relative]);
        try
        {
            var result = await new ScipAdapterRunner().RunAsync(
                EmptyIndex(),
                workspace,
                new ScipAdapterSpec(
                    "typescript",
                    executable,
                    ["index", workspace.Replace('\\', '/')],
                    UnspecifiedPositionEncoding: ScipPositionEncoding.Utf16),
                TestContext.Current.CancellationToken);

            Assert.Equal(SemanticAdapterState.Succeeded, result.Status.State);
            var symbols = result.Index.Occurrences
                .Select(occurrence => occurrence.SymbolId)
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .ToArray();
            Assert.Contains(symbols, symbol => symbol.Contains("greet", StringComparison.Ordinal));
            Assert.DoesNotContain(
                symbols,
                symbol =>
                {
                    var plain = symbol.Replace("`", string.Empty, StringComparison.Ordinal);
                    return plain.Contains(
                               root.Replace('\\', '/'),
                               StringComparison.OrdinalIgnoreCase) ||
                           plain.Contains(
                               workspace.Replace('\\', '/'),
                               StringComparison.OrdinalIgnoreCase);
                });
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SemanticIndex EmptyIndex() =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            BaseCommit = "commit",
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };
}
