using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts.Security;
using CodeSearch.Core.Search;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using CodeSearch.Cli;
using LocalAi.Repository;

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

// Scanned as an option of its own, stepping over the value of any option before it: `codesearch
// search --query "--json"` is an ordinary query. `localai` may scan its whole list, because
// nothing there could plausibly take that literal as a value.
//
// No language is pinned here, unlike `localai`: this binary prints English literals and has no
// catalogue to follow a reader. When it gains one, the pin belongs here.
var machineReadable = MachineEnvelope.RequestedAsOption(args);
if (machineReadable && !ConsoleJson.Supports(command))
{
    // The promise is unconditional or it is nothing: if the flag was passed, stdout is an
    // envelope. `index` and `overlay` stream progress to stdout as they build, so they could not
    // keep it even if they wanted to — and a plugin that wants an index built calls `localai sync`.
    Console.WriteLine(MachineEnvelope.Refusal(
        command,
        "json_not_supported",
        "--json is not available here. The usage marks the commands that take it with [--json]."));
    return 1;
}

var options = ParseOptions(
    (machineReadable ? MachineEnvelope.WithoutFlag(args) : args).Skip(1).ToArray());

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
        "capabilities" => Capabilities(),
        "scan" => Scan(options),
        "-h" or "--help" or "help" => PrintUsage(),
        _ => Fail($"Unknown command '{command}'.", "command_unknown"),
    };
}
catch (Exception ex)
{
    if (machineReadable)
    {
        Console.WriteLine(MachineEnvelope.Refusal(command, ConsoleJson.Classify(ex), ex.Message));
        return 1;
    }

    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

// The listing is for a program, and `--json` is how a program asks. Required rather than
// optional: a prose face would be a second inventory beside the usage block, and a second
// inventory is the thing that drifts.
//
// `json_required` can never appear inside an envelope, because the condition that produces it is
// the absence of one. This binary keeps its own convention of 1 for every failure.
//
// English, like every other string here: this console has no catalogue to follow a reader with,
// and a lone Russian sentence in a binary that cannot read one would be worse than none.
int Capabilities()
{
    if (!machineReadable)
    {
        Console.Error.WriteLine(
            "codesearch capabilities is for a program: add --json. " +
            "The usage block is the list for a person.");
        return 1;
    }

    Console.WriteLine(MachineEnvelope.Answer("capabilities", ConsoleJson.Capabilities()));
    return 0;
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
        return Fail(
            $"No base index at '{basePath}'. Build it with `localai sync`.",
            "index_not_built");
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
        return Fail("search needs --query \"...\"", "query_missing");
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

    if (machineReadable)
    {
        Console.WriteLine(MachineEnvelope.Answer("search", ConsoleJson.Describe(query, outcome)));
        return 0;
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
        return Fail("get-chunk needs --id <chunk_id>", "id_missing");
    }

    var chunk = await new SearchService().GetChunkAsync(
        chunkId,
        opts.GetValueOrDefault("root"));
    if (machineReadable)
    {
        Console.WriteLine(MachineEnvelope.Answer(
            "get-chunk",
            new ChunkData(
                chunk.ChunkId,
                chunk.RelPath,
                chunk.StartLine,
                chunk.EndLine,
                chunk.Kind.ToString(),
                chunk.Symbol,
                chunk.Signature,
                chunk.Body)));
        return 0;
    }

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

    // The bytes of a source file, which is the most directly injection-bearing thing either
    // console prints. The MCP tool that returns the same chunk has always wrapped it; this
    // printed it bare in every direction until now.
    Console.WriteLine(RedirectedSource.Wrap(
        $"get-chunk:{chunk.RelPath}",
        chunk.Body,
        Console.IsOutputRedirected));
    return 0;
}

async Task<int> EvaluateAsync(Dictionary<string, string> opts)
{
    if (!opts.TryGetValue("cases", out var casesPath) ||
        string.IsNullOrWhiteSpace(casesPath))
    {
        return Fail("evaluate needs --cases <json>", "cases_missing");
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
    var connected = Connected(status.WorkingRoot);
    if (machineReadable)
    {
        Console.WriteLine(MachineEnvelope.Answer("status", ConsoleJson.Describe(status, connected)));
        return 0;
    }

    // "Can I search here yet" is one question, and it used to take two commands: this one for the
    // index and `localai repo status` for whether the repository is connected at all. The verdict
    // uses that command's own two tokens, so one fact has one name in both binaries.
    Console.WriteLine($"Connected:    {(connected ? "CONFIGURED" : "NOT_CONFIGURED")}");
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
        ? "Base status:  STALE - the indexed commit is not HEAD; refresh with `localai sync`"
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
        Console.WriteLine("              own changes are invisible. Build it with " +
                          "`localai sync --root <this worktree>`.");
        return 0;
    }

    Console.WriteLine($"Overlay:      {status.Overlay.FileCount} files, {status.Overlay.ChunkCount} chunks, " +
                      $"{status.Overlay.DeletedCount} deletions, {status.Overlay.SizeBytes / 1024.0 / 1024.0:F1} MB");
    Console.WriteLine($"Overlay built: {status.Overlay.IndexedAtUtc:u} at commit {Short(status.Overlay.WorkingCommit)} " +
                      $"against base {Short(status.Overlay.BaseCommit)}");
    Console.WriteLine(status.Overlay.BaseDrifted(status.IndexedCommit)
        ? "Overlay status: STALE - the base moved since; refresh with `localai sync`"
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

/// <summary>
/// Whether LocalAi knows this repository, read the way `localai repo status` reads it. False for
/// anything that throws: an unconnected repository is exactly the case where the runtime layout
/// cannot be resolved, and a diagnostic command must not fail because its subject is absent.
/// </summary>
static bool Connected(string workingRoot)
{
    try
    {
        var identity = RuntimeIndexLayout.Inspect(workingRoot);
        return new RepositoryManifestStore(identity.RepositoryRuntimeRoot).Read() is not null;
    }
    catch (Exception exception) when (
        exception is InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return false;
    }
}

/// <summary>
/// A refusal in both faces. The prose keeps its `ERROR:` prefix and its usage block; the envelope
/// goes to standard output, because a caller that asked for JSON should never have to read
/// standard error to find out what happened.
///
/// Exit stays 1 whatever the failure: this binary has never had a code vocabulary of its own, and
/// giving it one is a separate subject from giving it an envelope.
/// </summary>
int Fail(string message, string code = "argument_unknown")
{
    if (machineReadable)
    {
        Console.WriteLine(MachineEnvelope.Refusal(command, code, message));
        return 1;
    }

    Console.Error.WriteLine($"ERROR: {message}");
    PrintUsage();
    return 1;
}

static int PrintUsage()
{
    Console.WriteLine(CodeSearchUsage.Text);

    return 0;
}
