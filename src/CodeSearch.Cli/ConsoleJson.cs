using System.Text.Json.Serialization;
using CodeSearch.Core.Search;
using LocalAi.Contracts;

namespace CodeSearch.Cli;

/// <summary>What one search hit tells a program.</summary>
public sealed record HitData(
    [property: JsonRequired, JsonPropertyName("chunkId"), JsonPropertyOrder(0)]
    string ChunkId,
    [property: JsonRequired, JsonPropertyName("path"), JsonPropertyOrder(1)]
    string Path,
    [property: JsonRequired, JsonPropertyName("startLine"), JsonPropertyOrder(2)]
    int StartLine,
    [property: JsonRequired, JsonPropertyName("endLine"), JsonPropertyOrder(3)]
    int EndLine,
    [property: JsonRequired, JsonPropertyName("kind"), JsonPropertyOrder(4)]
    string Kind,
    [property: JsonRequired, JsonPropertyName("symbol"), JsonPropertyOrder(5)]
    string Symbol,
    [property: JsonRequired, JsonPropertyName("signature"), JsonPropertyOrder(6)]
    string Signature,
    /// Comparable within one response and not between them: it is a cosine distance against this
    /// query's vector, so ordering means something and the absolute value does not.
    [property: JsonRequired, JsonPropertyName("score"), JsonPropertyOrder(7)]
    double Score);

/// <summary>
/// What <c>search</c> tells a program.
///
/// <c>embeddingsUsed</c> is required rather than convenient. The prose face puts "LEXICAL ONLY"
/// on standard error when no embedding model answered, and with <c>--json</c> standard error is
/// empty — so without this field a plugin would show literal matches as if they were ranked ones,
/// with nothing anywhere saying otherwise.
/// </summary>
public sealed record SearchData(
    [property: JsonRequired, JsonPropertyName("query"), JsonPropertyOrder(0)]
    string Query,
    [property: JsonRequired, JsonPropertyName("embeddingsUsed"), JsonPropertyOrder(1)]
    bool EmbeddingsUsed,
    [property: JsonRequired, JsonPropertyName("hits"), JsonPropertyOrder(2)]
    IReadOnlyList<HitData> Hits);

/// <summary>What <c>get-chunk</c> tells a program: the hit, and the source it names.</summary>
public sealed record ChunkData(
    [property: JsonRequired, JsonPropertyName("chunkId"), JsonPropertyOrder(0)]
    string ChunkId,
    [property: JsonRequired, JsonPropertyName("path"), JsonPropertyOrder(1)]
    string Path,
    [property: JsonRequired, JsonPropertyName("startLine"), JsonPropertyOrder(2)]
    int StartLine,
    [property: JsonRequired, JsonPropertyName("endLine"), JsonPropertyOrder(3)]
    int EndLine,
    [property: JsonRequired, JsonPropertyName("kind"), JsonPropertyOrder(4)]
    string Kind,
    [property: JsonRequired, JsonPropertyName("symbol"), JsonPropertyOrder(5)]
    string Symbol,
    [property: JsonRequired, JsonPropertyName("signature"), JsonPropertyOrder(6)]
    string Signature,
    /// Bare, as every enveloped answer is: structure is the boundary here, where the prose face
    /// needs markers.
    [property: JsonRequired, JsonPropertyName("body"), JsonPropertyOrder(7)]
    string Body);

/// <summary>The overlay, which is a thing with its own existence rather than six prefixed fields.</summary>
public sealed record OverlayData(
    [property: JsonRequired, JsonPropertyName("required"), JsonPropertyOrder(0)]
    bool Required,
    [property: JsonRequired, JsonPropertyName("built"), JsonPropertyOrder(1)]
    bool Built,
    [property: JsonRequired, JsonPropertyName("files"), JsonPropertyOrder(2)]
    int Files,
    [property: JsonRequired, JsonPropertyName("chunks"), JsonPropertyOrder(3)]
    int Chunks,
    [property: JsonRequired, JsonPropertyName("deletions"), JsonPropertyOrder(4)]
    int Deletions,
    [property: JsonRequired, JsonPropertyName("sizeBytes"), JsonPropertyOrder(5)]
    long SizeBytes,
    [property: JsonRequired, JsonPropertyName("stale"), JsonPropertyOrder(6)]
    bool Stale);

/// <summary>
/// What <c>status</c> tells a program — the whole of "can I search here yet", which until now took
/// two commands and a join.
/// </summary>
public sealed record StatusData(
    /// The same two tokens `localai repo status` prints. One fact, one name, in both binaries.
    [property: JsonRequired, JsonPropertyName("connected"), JsonPropertyOrder(0)]
    string Connected,
    [property: JsonRequired, JsonPropertyName("built"), JsonPropertyOrder(1)]
    bool Built,
    [property: JsonRequired, JsonPropertyName("stale"), JsonPropertyOrder(2)]
    bool Stale,
    /// `Precise` or `Heuristic`. A generation published without a semantic index — or with one
    /// that covers nothing — answers navigation from text matching while every other field here
    /// looks healthy. The two ways of being heuristic differ diagnostically but not in remedy, so
    /// the distinction stays in the prose.
    [property: JsonRequired, JsonPropertyName("navigation"), JsonPropertyOrder(3)]
    string Navigation,
    [property: JsonRequired, JsonPropertyName("workingRoot"), JsonPropertyOrder(4)]
    string WorkingRoot,
    [property: JsonRequired, JsonPropertyName("repositoryRoot"), JsonPropertyOrder(5)]
    string RepositoryRoot,
    [property: JsonRequired, JsonPropertyName("indexPath"), JsonPropertyOrder(6)]
    string IndexPath,
    [property: JsonRequired, JsonPropertyName("model"), JsonPropertyOrder(7)]
    string Model,
    [property: JsonRequired, JsonPropertyName("dimensions"), JsonPropertyOrder(8)]
    int Dimensions,
    [property: JsonRequired, JsonPropertyName("files"), JsonPropertyOrder(9)]
    int Files,
    [property: JsonRequired, JsonPropertyName("chunks"), JsonPropertyOrder(10)]
    int Chunks,
    [property: JsonRequired, JsonPropertyName("sizeBytes"), JsonPropertyOrder(11)]
    long SizeBytes,
    [property: JsonRequired, JsonPropertyName("indexedCommit"), JsonPropertyOrder(12)]
    string IndexedCommit,
    [property: JsonRequired, JsonPropertyName("currentCommit"), JsonPropertyOrder(13)]
    string CurrentCommit,
    [property: JsonRequired, JsonPropertyName("overlay"), JsonPropertyOrder(14)]
    OverlayData Overlay);

/// <summary>
/// Which of this binary's commands answer a program, and how their answers are shaped. The
/// envelope itself is <see cref="LocalAi.Contracts.MachineEnvelope"/>, shared with `localai`.
/// </summary>
public static class ConsoleJson
{
    /// <summary>
    /// <c>index</c> and <c>overlay</c> stream progress to standard output as they build, so an
    /// envelope at the end would leave a caller holding progress lines and an envelope — which
    /// breaks the one promise the flag makes. A plugin that wants an index built calls
    /// <c>localai sync</c>, which publishes a generation rather than writing a file in place.
    /// <c>evaluate</c> already prints a JSON shape of its own, and <c>scan</c> has nobody asking;
    /// every enveloped command is a <c>data</c> contract forever, and adding one later is
    /// additive.
    /// </summary>
    /// <summary>
    /// <c>capabilities</c> is in here like any other enveloped command, which is what makes the
    /// flag reach it through this same check and what puts it in its own answer with no special
    /// case anywhere.
    /// </summary>
    public static IReadOnlyList<string> Commands { get; } =
        ["search", "get-chunk", "status", "capabilities"];

    /// <summary>
    /// The shape that predates the envelope and still prints. Frozen: <c>evaluate</c> is a
    /// benchmark format, and nothing will join it.
    ///
    /// Listed rather than hidden — omitting it would leave a caller to hard-code the knowledge
    /// that this one spells its version field differently, which is the gap the listing closes.
    /// </summary>
    private static readonly string[] Legacy = ["evaluate"];

    /// <summary>
    /// The MCP tool that does the same job. A first copy rather than a second — this mapping is
    /// nowhere else in the code — and a test holds every name to <c>McpToolNames</c>, which the
    /// server's own attributes are held to in turn.
    ///
    /// <c>status</c> answers more than <c>index_status</c> does since it learned whether the
    /// repository is connected at all, so a caller falling back to the tool gets a narrower
    /// answer rather than a wrong one.
    /// </summary>
    private static readonly Dictionary<string, string> Tools = new(StringComparer.Ordinal)
    {
        ["search"] = "search_code",
        ["get-chunk"] = "get_code_chunk",
        ["status"] = "index_status",
    };

    public static bool Supports(string command) =>
        Commands.Contains(command, StringComparer.Ordinal);

    /// <summary>
    /// What this binary can be driven to do, derived from the lists above rather than written out
    /// beside them. `localai` answers the same question about itself, and neither can answer it
    /// about the other: the consoles do not reference each other, and a hard-coded copy of the
    /// sibling's surface is precisely what this exists to remove.
    /// </summary>
    public static CapabilityData Capabilities() =>
        CommandCapabilities.Describe("codesearch", Commands, Legacy, Tools);

    public static StatusData Describe(IndexStatus status, bool connected)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new StatusData(
            connected ? "CONFIGURED" : "NOT_CONFIGURED",
            status.Exists,
            status.CommitDrifted,
            status.SemanticIndexPresent && !status.SemanticIndexCoversNothing
                ? "Precise"
                : "Heuristic",
            status.WorkingRoot,
            status.RepositoryRoot,
            status.IndexPath,
            status.Model,
            status.Dim,
            status.FileCount,
            status.ChunkCount,
            status.SizeBytes,
            status.IndexedCommit,
            status.CurrentCommit,
            new OverlayData(
                status.RequiresOverlay,
                status.Overlay.Exists,
                status.Overlay.FileCount,
                status.Overlay.ChunkCount,
                status.Overlay.DeletedCount,
                status.Overlay.SizeBytes,
                status.Overlay.BaseDrifted(status.IndexedCommit)));
    }

    /// <summary>
    /// What a failure is called, so the flag's promise survives it: a caller that gets prose the
    /// moment something goes wrong has no machine mode at all.
    ///
    /// Only the two that a caller can act on are named. An index that was never built is answered
    /// by `localai sync`; one that is stale for this worktree is answered by syncing *this*
    /// worktree, and the message says so. Everything else is an unexpected failure, and pretending
    /// otherwise would need typed exceptions this core does not have — the same trade `localai`
    /// makes with `input_rejected`.
    /// </summary>
    public static string Classify(Exception exception) => exception switch
    {
        SearchNotReadyException => "index_not_ready",
        FileNotFoundException => "index_not_built",
        _ => "unexpected_failure",
    };

    public static SearchData Describe(string query, SearchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new SearchData(
            query,
            outcome.EmbeddingsUsed,
            outcome.Hits
                .Select(hit => new HitData(
                    hit.ChunkId,
                    hit.RelPath,
                    hit.StartLine,
                    hit.EndLine,
                    hit.Kind.ToString(),
                    hit.Symbol,
                    hit.Signature,
                    hit.VectorScore))
                .ToArray());
    }
}
