using CodeSearch.Core.Semantics;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Cli;

internal static class SemanticNavigationCommand
{
    public static int Execute(string[] args)
    {
        var operation = args.FirstOrDefault() ?? "status";
        var root = Option(args, "--root");
        if (string.Equals(operation, "config", StringComparison.Ordinal))
        {
            return Configure(args.Skip(1).ToArray());
        }

        if (string.Equals(operation, "lsp-config", StringComparison.Ordinal))
        {
            return ConfigureLanguageServers(args.Skip(1).ToArray());
        }

        if (string.Equals(operation, "fallback-config", StringComparison.Ordinal))
        {
            return ConfigureFallback(args.Skip(1).ToArray());
        }

        if (string.Equals(operation, "evaluate", StringComparison.Ordinal))
        {
            return Evaluate(args, root);
        }

        if (string.Equals(operation, "status", StringComparison.Ordinal))
        {
            try
            {
                var index = Load(root);
                Console.WriteLine($"semantic documents: {index.Documents.Count}");
                Console.WriteLine($"semantic symbols: {index.Symbols.Count}");
                Console.WriteLine($"semantic occurrences: {index.Occurrences.Count}");
                Console.WriteLine($"semantic relationships: {index.Relationships.Count}");
                Console.WriteLine($"snapshot tree: {index.GitTree}");
                Console.WriteLine($"snapshot dirty: {index.DirtyHash ?? "clean"}");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"semantic status failed: {exception.Message}");
                return 1;
            }
        }

        var path = Option(args, "--path");
        if (string.IsNullOrWhiteSpace(path) ||
            !int.TryParse(Option(args, "--line"), out var line) ||
            !int.TryParse(Option(args, "--column"), out var column))
        {
            return Usage();
        }

        try
        {
            var gateway = new SemanticNavigationGateway(
                heuristicNavigation: new HeuristicSemanticNavigation(
                    new HeuristicNavigationPolicyStore(
                        HeuristicNavigationPolicyStore.DefaultRuntimeRoot)));
            if (string.Equals(operation, "relationships", StringComparison.Ordinal))
            {
                var direction = Enum.Parse<SemanticRelationshipDirection>(
                    Option(args, "--direction") ?? "outgoing",
                    ignoreCase: true);
                var kindText = Option(args, "--kind");
                SemanticRelationshipKind? kind = kindText is null
                    ? null
                    : Enum.Parse<SemanticRelationshipKind>(
                        kindText.Replace("-", string.Empty, StringComparison.Ordinal),
                        ignoreCase: true);
                foreach (var related in gateway.FindRelationships(
                             path, line, column, direction, kind, root))
                {
                    var location = related.Location;
                    Console.WriteLine(
                        $"{location.DocumentPath}:{location.Range.StartLine}:" +
                        $"{location.Range.StartCharacter}-{location.Range.EndLine}:" +
                        $"{location.Range.EndCharacter} {related.Direction} " +
                        $"{related.Kind} {location.SymbolId}");
                }

                return 0;
            }

            var locations = operation switch
            {
                "definition" => gateway.GoToDefinition(path, line, column, root),
                "references" => gateway.FindReferences(
                    path,
                    line,
                    column,
                    !args.Contains("--exclude-definition", StringComparer.Ordinal),
                    root),
                "implementations" => gateway.FindImplementations(
                    path,
                    line,
                    column,
                    root),
                _ => throw new ArgumentException($"Unknown semantic operation '{operation}'."),
            };
            foreach (var location in locations)
            {
                Console.WriteLine(
                    $"{location.DocumentPath}:{location.Range.StartLine}:" +
                    $"{location.Range.StartCharacter}-{location.Range.EndLine}:" +
                    $"{location.Range.EndCharacter} {location.SymbolId}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"semantic {operation} failed: {exception.Message}");
            return 1;
        }
    }

    private static SemanticIndex Load(string? root)
    {
        var workingRoot = CodeSearch.Core.Indexing.RepoLocator.ResolveWorkingRoot(root);
        var identity = CodeSearch.Core.Indexing.RuntimeIndexLayout.Inspect(workingRoot);
        var store = new CodeSearch.Core.Indexing.GenerationStore(identity.RepositoryRuntimeRoot);
        var current = store.ReadCurrent()
            ?? throw new SemanticNavigationNotReadyException("No current generation is published.");
        var manifest = store.ReadManifest(current.GenerationId);
        var baseIndex = SemanticIndex.Load(store.SemanticIndexPath(current.GenerationId));
        var usesBaseSnapshot =
            string.Equals(identity.HeadTree, manifest.Identity.DevTree, StringComparison.Ordinal) &&
            identity.DirtyHash is null;
        return usesBaseSnapshot
            ? baseIndex
            : SemanticIndexOverlay.Load(
                CodeSearch.Core.Indexing.RuntimeIndexLayout.SemanticOverlayPath(
                    identity,
                    current.GenerationId))
                .Materialize(baseIndex);
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int Configure(string[] args)
    {
        var store = new SemanticIndexingPolicyStore(
            SemanticIndexingPolicyStore.DefaultRuntimeRoot);
        var operation = args.FirstOrDefault() ?? "show";
        if (string.Equals(operation, "path", StringComparison.Ordinal))
        {
            Console.WriteLine(store.Path);
            return 0;
        }

        if (string.Equals(operation, "init", StringComparison.Ordinal))
        {
            if (!File.Exists(store.Path))
            {
                store.Write(SemanticIndexingPolicy.Default);
            }

            Console.WriteLine(store.Path);
            return 0;
        }

        if (string.Equals(operation, "show", StringComparison.Ordinal))
        {
            Console.WriteLine($"config: {store.Path}");
            Console.WriteLine(JsonSerializer.Serialize(
                store.Read(),
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        return Usage();
    }

    private static int ConfigureLanguageServers(string[] args)
    {
        var store = new LanguageServerPolicyStore(LanguageServerPolicyStore.DefaultRuntimeRoot);
        var operation = args.FirstOrDefault() ?? "show";
        if (string.Equals(operation, "path", StringComparison.Ordinal))
        {
            Console.WriteLine(store.Path);
            return 0;
        }

        if (string.Equals(operation, "init", StringComparison.Ordinal))
        {
            if (!File.Exists(store.Path))
            {
                store.Write(LanguageServerPolicy.Default);
            }

            Console.WriteLine(store.Path);
            return 0;
        }

        if (string.Equals(operation, "show", StringComparison.Ordinal))
        {
            Console.WriteLine($"config: {store.Path}");
            Console.WriteLine(JsonSerializer.Serialize(
                store.Read(),
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        return Usage();
    }

    private static int ConfigureFallback(string[] args)
    {
        var store = new HeuristicNavigationPolicyStore(
            HeuristicNavigationPolicyStore.DefaultRuntimeRoot);
        var operation = args.FirstOrDefault() ?? "show";
        if (string.Equals(operation, "path", StringComparison.Ordinal))
        {
            Console.WriteLine(store.Path);
            return 0;
        }

        if (string.Equals(operation, "init", StringComparison.Ordinal))
        {
            if (!File.Exists(store.Path))
            {
                store.Write(HeuristicNavigationPolicy.Default);
            }

            Console.WriteLine(store.Path);
            return 0;
        }

        if (string.Equals(operation, "show", StringComparison.Ordinal))
        {
            Console.WriteLine($"config: {store.Path}");
            Console.WriteLine(JsonSerializer.Serialize(
                store.Read(),
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        return Usage();
    }

    private static int Evaluate(string[] args, string? root)
    {
        var casesPath = Option(args, "--cases");
        if (string.IsNullOrWhiteSpace(casesPath))
        {
            return Usage();
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                Converters = { new JsonStringEnumConverter() },
            };
            var suite = JsonSerializer.Deserialize<SemanticBenchmarkSuite>(
                File.ReadAllBytes(Path.GetFullPath(casesPath)), options)
                ?? throw new InvalidDataException("Semantic benchmark suite is empty.");
            if (int.TryParse(Option(args, "--iterations"), out var iterations))
            {
                suite = suite with { Iterations = iterations };
            }

            var workingRoot = CodeSearch.Core.Indexing.RepoLocator.ResolveWorkingRoot(root);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var managedBefore = GC.GetTotalMemory(forceFullCollection: false);
            var workingSetBefore = process.WorkingSet64;
            var stopwatch = Stopwatch.StartNew();
            var index = Load(workingRoot);
            stopwatch.Stop();
            process.Refresh();
            var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
            var workingSetAfter = process.WorkingSet64;

            var result = new SemanticNavigationBenchmark(index).Run(workingRoot, suite);
            var report = new
            {
                SchemaVersion = 1,
                MeasuredAtUtc = DateTime.UtcNow,
                RepositoryRoot = workingRoot,
                Snapshot = new { index.GitTree, index.DirtyHash },
                Index = new
                {
                    Bytes = SemanticIndexBytes(workingRoot),
                    Documents = index.Documents.Count,
                    Symbols = index.Symbols.Count,
                    Occurrences = index.Occurrences.Count,
                    Relationships = index.Relationships.Count,
                },
                ColdLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                ManagedMemoryDeltaBytes = managedAfter - managedBefore,
                WorkingSetDeltaBytes = workingSetAfter - workingSetBefore,
                result.Passed,
                result.Total,
                result.Correctness,
                result.Cases,
            };
            Console.WriteLine(JsonSerializer.Serialize(report, options));
            return result.Passed == result.Total ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"semantic evaluate failed: {exception.Message}");
            return 1;
        }
    }

    private static long SemanticIndexBytes(string root)
    {
        var identity = CodeSearch.Core.Indexing.RuntimeIndexLayout.Inspect(root);
        var store = new CodeSearch.Core.Indexing.GenerationStore(identity.RepositoryRuntimeRoot);
        var current = store.ReadCurrent()
            ?? throw new SemanticNavigationNotReadyException("No current generation is published.");
        var paths = new[]
        {
            store.SemanticIndexPath(current.GenerationId),
            CodeSearch.Core.Indexing.RuntimeIndexLayout.SemanticOverlayPath(
                identity, current.GenerationId),
        };
        return paths.Where(File.Exists).Sum(path => new FileInfo(path).Length);
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: localai semantic status [--root <path>]\n" +
            "       localai semantic config show|path|init\n" +
            "       localai semantic lsp-config show|path|init\n" +
            "       localai semantic fallback-config show|path|init\n" +
            "       localai semantic evaluate --cases <json> [--iterations <n>] [--root <path>]\n" +
            "       localai semantic definition --path <relative> --line <zero-based> " +
            "--column <utf16> [--root <path>]\n" +
            "       localai semantic references --path <relative> --line <zero-based> " +
            "--column <utf16> [--exclude-definition] [--root <path>]\n" +
            "       localai semantic implementations --path <relative> --line <zero-based> " +
            "--column <utf16> [--root <path>]\n" +
            "       localai semantic relationships --path <relative> --line <zero-based> " +
            "--column <utf16> [--direction incoming|outgoing] " +
            "[--kind implementation|override|type-definition] [--root <path>]");
        return 2;
    }
}
