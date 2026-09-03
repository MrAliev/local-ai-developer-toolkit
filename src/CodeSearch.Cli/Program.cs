using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

// Redirected stdout on Windows otherwise falls back to the legacy console codepage and mangles
// every Cyrillic character - which matters, this codebase's comments are mostly Russian.
ConsoleOutputText.UseUtf8();

// Quality-first default, and it fits a single 16GB card - the bare `:8b` tag is Q4_K_M, and fp16
// only runs by splitting across two GPUs, which this machine will not have for much longer.
const string DefaultModel = "qwen3-embedding:8b-q8_0";

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();
var options = ParseOptions(args.Skip(1).ToArray());

try
{
    return command switch
    {
        "index" => await IndexAsync(options),
        "overlay" => await OverlayAsync(options),
        "search" => await SearchAsync(options),
        "get-chunk" => await GetChunkAsync(options),
        "evaluate" => await EvaluateAsync(options),
        "status" => Status(options),
        "scan" => Scan(options),
        "-h" or "--help" or "help" => PrintUsage(),
        _ => Fail($"Unknown command '{command}'."),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

async Task<int> IndexAsync(Dictionary<string, string> opts)
{
    var root = RepoLocator.ResolveRoot(opts.GetValueOrDefault("root")).Value;
    var indexPath = opts.GetValueOrDefault("index") ?? RepoLocator.IndexPathFor(root);
    var model = opts.GetValueOrDefault("model") ?? DefaultModel;
    var force = opts.ContainsKey("force");

    Console.WriteLine($"Repository: {root}");
    Console.WriteLine($"Index:      {indexPath}");
    Console.WriteLine($"Model:      {model}");

    var embedder = CreateEmbedder(model);
    var builder = new IndexBuilder(embedder, Console.WriteLine);
    var result = await builder.BuildAsync(root, indexPath, force);

    Console.WriteLine();
    Console.WriteLine($"Files indexed:    {result.FileCount}");
    Console.WriteLine($"Chunks:           {result.ChunkCount}");
    Console.WriteLine($"Files reused:     {result.FilesReused}");
    Console.WriteLine($"Files re-embedded:{result.FilesEmbedded}");
    Console.WriteLine($"Chunks embedded:  {result.ChunksEmbedded}");
    Console.WriteLine($"Elapsed:          {result.Elapsed.TotalMinutes:F1} min");
    return 0;
}

async Task<int> OverlayAsync(Dictionary<string, string> opts)
{
    var workingRoot = RepoLocator.ResolveWorkingRoot(opts.GetValueOrDefault("root")).Value;
    var basePath = opts.GetValueOrDefault("index")
        ?? RepoLocator.IndexPathFor(RepoLocator.ResolveRoot(opts.GetValueOrDefault("root")).Value);
    var overlayPath = opts.GetValueOrDefault("overlay") ?? RepoLocator.OverlayPathFor(workingRoot);

    if (!File.Exists(basePath))
    {
        return Fail($"No base index at '{basePath}'. Build it first with `codesearch index`.");
    }

    // The model is dictated by the base: an overlay embedded with anything else produces vectors
    // that cannot be compared against it.
    var model = CodeIndex.Load(basePath, withVectors: false).Model;

    Console.WriteLine($"Working root: {workingRoot}");
    Console.WriteLine($"Base index:   {basePath}");
    Console.WriteLine($"Overlay:      {overlayPath}");
    Console.WriteLine($"Model:        {model} (from the base)");

    var embedder = CreateEmbedder(model);
    var builder = new IndexBuilder(embedder, Console.WriteLine);
    var result = await builder.BuildOverlayAsync(workingRoot, basePath, overlayPath);

    Console.WriteLine();
    Console.WriteLine($"Files in overlay: {result.FileCount}");
    Console.WriteLine($"Chunks:           {result.ChunkCount}");
    Console.WriteLine($"Elapsed:          {result.Elapsed.TotalSeconds:F1}s");
    return 0;
}

async Task<int> SearchAsync(Dictionary<string, string> opts)
{
    if (!opts.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
    {
        return Fail("search needs --query \"...\"");
    }

    var service = new SearchService()
    {
        UseQueryInstruction = !opts.ContainsKey("no-instruct"),
    };

    var searchOptions = new SearchOptions
    {
        TopK = int.TryParse(opts.GetValueOrDefault("top"), out var top) ? top : 10,
        Kind = ParseKind(opts.GetValueOrDefault("kind")),
        PathContains = opts.GetValueOrDefault("path"),
        MaxPerFile = int.TryParse(opts.GetValueOrDefault("per-file"), out var perFile) ? perFile : 3,
    };

    var outcome = await service.SearchAsync(query, opts.GetValueOrDefault("root"), searchOptions);
    if (!outcome.EmbeddingsUsed)
    {
        // To stderr, so redirecting the hits to a file still leaves the reason on the screen.
        Console.Error.WriteLine(
            "LEXICAL ONLY: no embedding model answered, so this search matched the words " +
            "of the query literally and nothing else. Check the broker: localai doctor");
    }

    if (outcome.Hits.Count == 0)
    {
        Console.WriteLine("No matches.");
        return 0;
    }

    foreach (var hit in outcome.Hits)
    {
        Console.WriteLine($"{hit.RelPath}:{hit.StartLine}-{hit.EndLine}  [{hit.Kind}]  cos={hit.VectorScore:F3}");
        Console.WriteLine($"  {hit.Symbol}");
        Console.WriteLine($"  {hit.Signature}");
        Console.WriteLine($"  chunk_id: {hit.ChunkId}");
        Console.WriteLine();
    }

    return 0;
}

async Task<int> GetChunkAsync(Dictionary<string, string> opts)
{
    if (!opts.TryGetValue("id", out var chunkId) ||
        string.IsNullOrWhiteSpace(chunkId))
    {
        return Fail("get-chunk needs --id <chunk_id>");
    }

    var chunk = await new SearchService().GetChunkAsync(
        chunkId,
        opts.GetValueOrDefault("root"));
    Console.WriteLine(
        $"{chunk.RelPath}:{chunk.StartLine}-{chunk.EndLine}  [{chunk.Kind}]");
    Console.WriteLine(chunk.Symbol);
    if (chunk.Signature.Length > 0 &&
        chunk.Signature != chunk.Symbol)
    {
        Console.WriteLine(chunk.Signature);
    }

    Console.WriteLine($"chunk_id: {chunk.ChunkId}");
    Console.WriteLine();
    Console.WriteLine(chunk.Body);
    return 0;
}

async Task<int> EvaluateAsync(Dictionary<string, string> opts)
{
    if (!opts.TryGetValue("cases", out var casesPath) ||
        string.IsNullOrWhiteSpace(casesPath))
    {
        return Fail("evaluate needs --cases <json>");
    }

    if (opts.ContainsKey("no-floor") && opts.ContainsKey("profile"))
    {
        return Fail(
            "evaluate accepts either --no-floor or --profile, not both.");
    }

    var noFloor = opts.ContainsKey("no-floor");
    var root = RepoLocator.ResolveWorkingRoot(opts.GetValueOrDefault("root")).Value;
    var corpus = SearchEvaluationCorpus.Load(Path.GetFullPath(casesPath));
    SearchEvaluationCorpus.ValidateAgainstSource(corpus, root);
    var service = new SearchService
    {
        UseQueryInstruction = !opts.ContainsKey("no-instruct")
    };
    var searchOptions = SearchEvaluation.CreateSearchOptions(noFloor);
    var observations = new List<SearchEvaluationObservation>(corpus.Cases.Count);

    foreach (var item in corpus.Cases)
    {
        var timer = Stopwatch.StartNew();
        var outcome = await service.SearchAsync(item.Query, root, searchOptions);
        timer.Stop();
        if (!outcome.EmbeddingsUsed)
        {
            // Scoring retrieval quality against a search that never embedded measures the
            // lexical matcher and reports it as the model's. Refusing is the only honest
            // answer: a number produced this way is worse than no number.
            Console.Error.WriteLine(
                "evaluation stopped: no embedding model answered, so these queries would " +
                "score the literal matcher rather than the index. Check the broker: " +
                "localai doctor");
            return 75;
        }

        observations.Add(
            new SearchEvaluationObservation(
                item.Id,
                outcome.Hits.Select(SearchEvaluation.FromSearchHit).ToArray(),
                timer.Elapsed,
                null));
    }

    var status = service.Status(root);
    var output = new
    {
        schemaVersion = 1,
        mode = noFloor ? "no-floor" : "profile",
        corpusSchemaVersion = corpus.SchemaVersion,
        caseCount = corpus.Cases.Count,
        repository = status.RepositoryRoot,
        generationId = CodeIndex.Load(status.IndexPath, withVectors: false).GenerationId,
        model = status.Model,
        brokerQueueWait = "unavailable: embedding client does not expose receipt telemetry",
        tokenEstimator = new
        {
            point = "ceil(responseCharacters / 4)",
            lowerBound = "ceil(responseCharacters / 6)",
            upperBound = "ceil(responseCharacters / 3)"
        },
        metrics = SearchEvaluation.Measure(corpus, observations),
        observations
    };
    Console.WriteLine(
        JsonSerializer.Serialize(
            output,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    return 0;
}

int Status(Dictionary<string, string> opts)
{
    var status = new SearchService().Status(opts.GetValueOrDefault("root"));
    Console.WriteLine($"Working root: {status.WorkingRoot}");
    Console.WriteLine($"Repository:   {status.RepositoryRoot}");
    Console.WriteLine($"Base index:   {status.IndexPath}");

    if (!status.Exists)
    {
        Console.WriteLine("Base:         NOT BUILT");
        return 0;
    }

    Console.WriteLine($"Base built from: {status.BaseRoot}");
    Console.WriteLine($"Model:        {status.Model} ({status.Dim} dims)");
    Console.WriteLine($"Base:         {status.FileCount} files, {status.ChunkCount} chunks, " +
                      $"{status.SizeBytes / 1024.0 / 1024.0:F1} MB, commit {Short(status.IndexedCommit)}");
    Console.WriteLine(status.CommitDrifted
        ? "Base status:  STALE - rerun `codesearch index` against the base checkout"
        : "Base status:  current");

    if (!status.RequiresOverlay)
    {
        Console.WriteLine("Overlay:      not needed - worktree matches the clean base");
        return 0;
    }

    if (!status.Overlay.Exists)
    {
        Console.WriteLine($"Overlay:      NOT BUILT ({status.Overlay.Path})");
        Console.WriteLine("              searches here answer from the base only, so this branch's");
        Console.WriteLine("              own changes are invisible. Build it with `codesearch overlay`.");
        return 0;
    }

    Console.WriteLine($"Overlay:      {status.Overlay.FileCount} files, {status.Overlay.ChunkCount} chunks, " +
                      $"{status.Overlay.DeletedCount} deletions, {status.Overlay.SizeBytes / 1024.0 / 1024.0:F1} MB");
    Console.WriteLine($"Overlay built: {status.Overlay.IndexedAtUtc:u} at commit {Short(status.Overlay.WorkingCommit)} " +
                      $"against base {Short(status.Overlay.BaseCommit)}");
    Console.WriteLine(status.Overlay.BaseDrifted(status.IndexedCommit)
        ? "Overlay status: STALE - the base moved since; rerun `codesearch overlay`"
        : "Overlay status: current");
    return 0;
}

int Scan(Dictionary<string, string> opts)
{
    var root = RepoLocator.ResolveWorkingRoot(opts.GetValueOrDefault("root")).Value;
    var files = FileScanner.Enumerate(root);
    Console.WriteLine($"Working root: {root}");
    Console.WriteLine($"Indexable files: {files.Count}");
    return 0;
}

static string Short(string commit) => commit.Length >= 9 ? commit[..9] : commit;

static ChunkKind? ParseKind(string? value) =>
    Enum.TryParse<ChunkKind>(value, ignoreCase: true, out var kind) ? kind : null;

static IEmbeddingClient CreateEmbedder(string model) =>
    new BrokerEmbeddingClient(model, BrokerClientFactory.CreateDefault());

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        result[key] = hasValue ? args[++i] : "true";
    }

    return result;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"ERROR: {message}");
    PrintUsage();
    return 1;
}

static int PrintUsage()
{
    Console.WriteLine("""
        codesearch - semantic + literal code search over a local repository.

          codesearch index   [--root <dir>] [--model <ollama-model>] [--force] [--index <file>]
          codesearch overlay [--root <dir>] [--index <base>] [--overlay <file>]
          codesearch search   --query "<text>" [--root <dir>] [--top N] [--kind Type|Method|Text|File]
                              [--path <substring>] [--per-file N] [--no-instruct]
          codesearch get-chunk --id <chunk_id> [--root <dir>]
          codesearch evaluate --cases <json> [--root <dir>] [--profile|--no-floor] [--no-instruct]
          codesearch status  [--root <dir>]
          codesearch scan    [--root <dir>]

        One BASE index per repository, built from the mainline checkout, lives in
        ~/.claude/tools/index. Every other worktree keeps an OVERLAY in its own
        .claude/codesearch/overlay.cidx holding only what its branch changed, plus the files it
        deleted. Searches see the overlay laid over the base, so a branch pays for its diff
        rather than for a second full index.

        Build the base against the mainline checkout:
          codesearch index --root <mainline-worktree> --index <canonical path from `status`>
        """);

    return 0;
}
