using LocalAi.Contracts.Localization;

namespace CodeSearch.Mcp.Resources;

/// <summary>
/// What the CodeSearch tools say, in the language the reader's machine is set to.
///
/// One rule decides what is in here and what stays written into the code: anything to the left of
/// the first colon on a line is a field name and stays English, and so does any status token,
/// enum name, identifier, path or command to the right of one. Sentences and clauses are
/// translated.
///
/// That is not thrift. The instruction block this product installs into every agent
/// configuration is English-only and keys agent behaviour on the exact tokens <c>STALE</c>,
/// <c>INITIALIZING</c> and <c>CONFIGURED</c>, and the shipped Russian documentation quotes
/// <c>CONFIGURED</c> and <c>Update:</c> as literals inside Russian prose. Translating the field
/// names would break, for a Russian reader only, the rule their agent was installed with — and
/// it would widen the aligned gutter from twelve characters to twenty for a column whose values
/// are mostly identifiers.
/// </summary>
public static class CodeSearchText
{
    public static TextCatalogue Catalogue { get; } = new(
        "CodeSearch.Mcp.Resources.CodeSearchText",
        typeof(CodeSearchText).Assembly);

    public static string NoDefinition => Catalogue.Get(nameof(NoDefinition));

    public static string NoReferences => Catalogue.Get(nameof(NoReferences));

    public static string NoImplementations => Catalogue.Get(nameof(NoImplementations));

    public static string NoRelationships => Catalogue.Get(nameof(NoRelationships));

    public static string NoMatches => Catalogue.Get(nameof(NoMatches));

    /// <summary>
    /// One sentence for all eight tools, with the tool's own name in it. It used to be eight
    /// literals, one of which said "Search failed" for a tool called <c>search_code</c>.
    /// </summary>
    public static string ToolFailed(string tool, string reason) =>
        Catalogue.Format(nameof(ToolFailed), tool, reason);

    public static string LspDocumentOpen(string path, int version, string languageId) =>
        Catalogue.Format(nameof(LspDocumentOpen), path, version, languageId);

    public static string LspDocumentClosed(string path) =>
        Catalogue.Format(nameof(LspDocumentClosed), path);

    public static string SearchIndexHeader(int chunks, int files, string model, string elapsed) =>
        Catalogue.Format(nameof(SearchIndexHeader), chunks, files, model, elapsed);

    public static string SearchIndexHeaderStale(string indexed, string head) =>
        Catalogue.Format(nameof(SearchIndexHeaderStale), indexed, head);

    public static string Seconds(string seconds) => Catalogue.Format(nameof(Seconds), seconds);

    public static string NoIndexForRoot(string root) =>
        Catalogue.Format(nameof(NoIndexForRoot), root);

    public static string BuildItBackground => Catalogue.Get(nameof(BuildItBackground));

    public static string BuildItWith => Catalogue.Get(nameof(BuildItWith));

    public static string StatusStale(string indexed, string head) =>
        Catalogue.Format(nameof(StatusStale), indexed, head);

    public static string ModelWithDims(string model, int dimensions) =>
        Catalogue.Format(nameof(ModelWithDims), model, dimensions);

    public static string SizeMegabytes(string megabytes) =>
        Catalogue.Format(nameof(SizeMegabytes), megabytes);

    public static string BuiltAtCommit(string built, string commit) =>
        Catalogue.Format(nameof(BuiltAtCommit), built, commit);

    public static string NavigationPrecise => Catalogue.Get(nameof(NavigationPrecise));

    public static string NavigationHeuristicCoversNothing(string root) =>
        Catalogue.Format(nameof(NavigationHeuristicCoversNothing), root);

    public static string NavigationHeuristicMissing(string root) =>
        Catalogue.Format(nameof(NavigationHeuristicMissing), root);

    public static string SyncReached(object phase, string when) =>
        Catalogue.Format(nameof(SyncReached), phase, when);

    public static string ProgressNotCounted => Catalogue.Get(nameof(ProgressNotCounted));

    public static string ProgressChunks(int processed, int total, int remaining) =>
        Catalogue.Format(nameof(ProgressChunks), processed, total, remaining);

    public static string RateChunks(string rate) => Catalogue.Format(nameof(RateChunks), rate);

    public static string EtaMinutes(string minutes) =>
        Catalogue.Format(nameof(EtaMinutes), minutes);

    public static string EtaCalculating => Catalogue.Get(nameof(EtaCalculating));

    public static string NothingLoaded => Catalogue.Get(nameof(NothingLoaded));

    public static string WorkingSetTrimmed => Catalogue.Get(nameof(WorkingSetTrimmed));

    public static string WorkingSetNotTrimmed => Catalogue.Get(nameof(WorkingSetNotTrimmed));

    public static string Unloaded(string names) => Catalogue.Format(nameof(Unloaded), names);

    public static string ProcessMemory(long freed, long remaining, string trim) =>
        Catalogue.Format(nameof(ProcessMemory), freed, remaining, trim);

    public static string ReloadsInASecond => Catalogue.Get(nameof(ReloadsInASecond));

    public static string CliUnavailable(string executable) =>
        Catalogue.Format(nameof(CliUnavailable), executable);

    public static string SyncFailed(int exitCode, string error) =>
        Catalogue.Format(nameof(SyncFailed), exitCode, error);

    public static string StatusNotRefreshed(int files, int limit) =>
        Catalogue.Format(nameof(StatusNotRefreshed), files, limit);

    public static string NothingHappened => Catalogue.Get(nameof(NothingHappened));

    public static string TooBigForToolCall => Catalogue.Get(nameof(TooBigForToolCall));

    public static string RunInBackground => Catalogue.Get(nameof(RunInBackground));

    public static string WhileItRuns => Catalogue.Get(nameof(WhileItRuns));

    /// <summary>
    /// The three values are nullable on the way in and were interpolated as such before, which
    /// renders a missing one as nothing rather than as the word "null".
    /// </summary>
    public static string UpdateAvailable(string? latest, string? installed, string? url) =>
        Catalogue.Format(nameof(UpdateAvailable), latest, installed, url);
}
