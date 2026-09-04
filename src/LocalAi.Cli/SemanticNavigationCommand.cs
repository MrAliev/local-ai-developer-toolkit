using CodeSearch.Core.Semantics;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Cli.Resources;

namespace LocalAi.Cli;

internal static class SemanticNavigationCommand
{
    /// <param name="runtimeRoot">
    /// The installation whose policy files and published generation this command reads. Null
    /// means the machine's own, which is the only value the CLI passes.
    /// </param>
    public static int Execute(string[] args, string? runtimeRoot = null)
    {
        var operation = args.FirstOrDefault() ?? "status";
        var root = Option(args, "--root");
        if (string.Equals(operation, "config", StringComparison.Ordinal))
        {
            return Configure(args.Skip(1).ToArray(), runtimeRoot);
        }

        if (string.Equals(operation, "lsp-config", StringComparison.Ordinal))
        {
            return ConfigureLanguageServers(args.Skip(1).ToArray(), runtimeRoot);
        }

        if (string.Equals(operation, "fallback-config", StringComparison.Ordinal))
        {
            return ConfigureFallback(args.Skip(1).ToArray(), runtimeRoot);
        }

        if (string.Equals(operation, "evaluate", StringComparison.Ordinal))
        {
            return Evaluate(args, root, runtimeRoot);
        }

        if (string.Equals(operation, "status", StringComparison.Ordinal))
        {
            try
            {
                var index = Load(root, runtimeRoot);
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
                Console.Error.WriteLine(
                    CliText.SemanticCommandFailed("status", exception.Message));
                return 1;
            }
        }

        if (!NavigationOperations.Contains(operation, StringComparer.Ordinal))
        {
            Console.Error.WriteLine(CliText.SemanticOperationUnknown(operation));
            return Usage();
        }

        var path = Option(args, "--path");
        if (string.IsNullOrWhiteSpace(path) ||
            !int.TryParse(Option(args, "--line"), out var line) ||
            !int.TryParse(Option(args, "--column"), out var column))
        {
            return Usage();
        }

        // The same path rule and the same numbering the MCP tools take, because this command is
        // what the instruction block offers when that server is unreachable. A fallback that
        // reads a position differently from the tool it stands in for is a trap set for the
        // moment somebody is already dealing with a breakage.
        if (!SemanticDocumentPath.IsRepositoryRelative(path))
        {
            Console.Error.WriteLine(CliText.PathNotRelative(path));
            return 2;
        }

        if (!SourcePosition.TryFromOneBased(line, column, out var position))
        {
            Console.Error.WriteLine(CliText.PositionNotFromOne(line, column));
            return 2;
        }

        try
        {
            var gateway = new SemanticNavigationGateway(
                heuristicNavigation: new HeuristicSemanticNavigation(
                    new HeuristicNavigationPolicyStore(Runtime(runtimeRoot))),
                runtimeRoot: runtimeRoot);
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
                             path, position.Line, position.Utf16Column, direction, kind, root))
                {
                    var location = related.Location;
                    Console.WriteLine(
                        $"{location.DocumentPath}:" +
                        $"{SourcePosition.ToOneBased(location.Range.StartLine)}:" +
                        $"{SourcePosition.ToOneBased(location.Range.StartCharacter)}-" +
                        $"{SourcePosition.ToOneBased(location.Range.EndLine)}:" +
                        $"{SourcePosition.ToOneBased(location.Range.EndCharacter)} " +
                        $"{related.Direction} " +
                        $"{related.Kind} {location.SymbolId}");
                }

                return 0;
            }

            var locations = operation switch
            {
                "definition" => gateway.GoToDefinition(path, position.Line, position.Utf16Column, root),
                "references" => gateway.FindReferences(
                    path,
                    position.Line,
                    position.Utf16Column,
                    !args.Contains("--exclude-definition", StringComparer.Ordinal),
                    root),
                "implementations" => gateway.FindImplementations(
                    path,
                    position.Line,
                    position.Utf16Column,
                    root),
                _ => throw new UnreachableException(
                    "The operation was tested before this switch was entered."),
            };
            foreach (var location in locations)
            {
                Console.WriteLine(
                    $"{location.DocumentPath}:" +
                    $"{SourcePosition.ToOneBased(location.Range.StartLine)}:" +
                    $"{SourcePosition.ToOneBased(location.Range.StartCharacter)}-" +
                    $"{SourcePosition.ToOneBased(location.Range.EndLine)}:" +
                    $"{SourcePosition.ToOneBased(location.Range.EndCharacter)} " +
                    $"{location.SymbolId}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                CliText.SemanticCommandFailed(operation, exception.Message));
            return 1;
        }
    }

    /// <summary>
    /// The installation a command works against: the one it was given, or the machine's own.
    /// </summary>
    private static string Runtime(string? runtimeRoot) =>
        string.IsNullOrWhiteSpace(runtimeRoot)
            ? SemanticIndexingPolicyStore.DefaultRuntimeRoot
            : runtimeRoot;

    private static SemanticIndex Load(string? root, string? runtimeRoot)
    {
        var workingRoot = CodeSearch.Core.Indexing.RepoLocator.ResolveWorkingRoot(root).Value;
        var identity = CodeSearch.Core.Indexing.RuntimeIndexLayout.Inspect(
            workingRoot,
            runtimeRoot);
        var store = new CodeSearch.Core.Indexing.GenerationStore(identity.RepositoryRuntimeRoot);
        var current = store.ReadCurrent()
            ?? throw new SemanticNavigationNotReadyException(CliText.NoCurrentGeneration);
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

    private static int Configure(string[] args, string? runtimeRoot)
    {
        var store = new SemanticIndexingPolicyStore(Runtime(runtimeRoot));
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

    private static int ConfigureLanguageServers(string[] args, string? runtimeRoot)
    {
        var store = new LanguageServerPolicyStore(Runtime(runtimeRoot));
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

    private static int ConfigureFallback(string[] args, string? runtimeRoot)
    {
        var store = new HeuristicNavigationPolicyStore(Runtime(runtimeRoot));
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

    private static int Evaluate(string[] args, string? root, string? runtimeRoot)
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
                ?? throw new InvalidDataException(
                    CliText.SemanticEvaluateNoSuite(casesPath));
            if (int.TryParse(Option(args, "--iterations"), out var iterations))
            {
                suite = suite with { Iterations = iterations };
            }

            var workingRoot = CodeSearch.Core.Indexing.RepoLocator.ResolveWorkingRoot(root).Value;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var managedBefore = GC.GetTotalMemory(forceFullCollection: false);
            var workingSetBefore = process.WorkingSet64;
            var stopwatch = Stopwatch.StartNew();
            var index = Load(workingRoot, runtimeRoot);
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
                    Bytes = SemanticIndexBytes(workingRoot, runtimeRoot),
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
            Console.Error.WriteLine(
                CliText.SemanticCommandFailed("evaluate", exception.Message));
            return 1;
        }
    }

    private static long SemanticIndexBytes(string root, string? runtimeRoot)
    {
        var identity = CodeSearch.Core.Indexing.RuntimeIndexLayout.Inspect(root, runtimeRoot);
        var store = new CodeSearch.Core.Indexing.GenerationStore(identity.RepositoryRuntimeRoot);
        var current = store.ReadCurrent()
            ?? throw new SemanticNavigationNotReadyException(CliText.NoCurrentGeneration);
        var paths = new[]
        {
            store.SemanticIndexPath(current.GenerationId),
            CodeSearch.Core.Indexing.RuntimeIndexLayout.SemanticOverlayPath(
                identity, current.GenerationId),
        };
        return paths.Where(File.Exists).Sum(path => new FileInfo(path).Length);
    }

    private static readonly string[] NavigationOperations =
        ["definition", "references", "implementations", "relationships"];

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: localai semantic status [--root <path>]\n" +
            "       localai semantic config show|path|init\n" +
            "       localai semantic lsp-config show|path|init\n" +
            "       localai semantic fallback-config show|path|init\n" +
            "       localai semantic evaluate --cases <json> [--iterations <n>] [--root <path>]\n" +
            "       localai semantic definition --path <relative> --line <1-based> " +
            "--column <1-based utf16> [--root <path>]\n" +
            "       localai semantic references --path <relative> --line <1-based> " +
            "--column <1-based utf16> [--exclude-definition] [--root <path>]\n" +
            "       localai semantic implementations --path <relative> --line <1-based> " +
            "--column <1-based utf16> [--root <path>]\n" +
            "       localai semantic relationships --path <relative> --line <1-based> " +
            "--column <1-based utf16> [--direction incoming|outgoing] " +
            "[--kind implementation|override|type-definition] [--root <path>]");
        return 2;
    }
}
