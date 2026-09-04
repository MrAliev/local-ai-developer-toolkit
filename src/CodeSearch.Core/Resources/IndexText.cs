using LocalAi.Contracts.Localization;

namespace CodeSearch.Core.Resources;

/// <summary>
/// What this assembly refuses with, in the language the reader's machine is set to.
///
/// These sentences do not stay inside the process that throws them: the MCP tools print them as
/// the <c>{ex.Message}</c> half of <c>semantic_navigation_not_ready:</c> and its siblings, and
/// <c>codesearch --json</c> puts them in <c>error.message</c>. Until this catalogue existed they
/// were the last place a Russian reader got a Russian answer with an English cause.
///
/// The rule for what belongs here is <c>CodeSearchText</c>'s, unchanged: anything left of the
/// first colon is a field name or a code and stays English, and so does any status token,
/// identifier, path or command to the right of one. Sentences and clauses are translated. There
/// is deliberately not a second rule.
///
/// Not everything this assembly throws is here. A refusal no entry point can produce has no
/// reader, and a string translated for nobody is cost with a parity test guarding it — so
/// <c>SearchQualityProfile</c>'s <c>MinVectorScore</c> refusal and both of
/// <c>SearchReadinessGate</c>'s stay English literals: the first is reachable only by code
/// calling <c>Resolve</c> directly, and the second by a type nothing in <c>src/</c> constructs.
/// </summary>
public static class IndexText
{
    public static TextCatalogue Catalogue { get; } = new(
        "CodeSearch.Core.Resources.IndexText",
        typeof(IndexText).Assembly);

    public static string GenerationNotPublished(string workingRoot) =>
        Catalogue.Format(nameof(GenerationNotPublished), workingRoot);

    public static string GenerationWithoutSemanticIndex(string generationId, string workingRoot) =>
        Catalogue.Format(nameof(GenerationWithoutSemanticIndex), generationId, workingRoot);

    public static string SemanticIndexUnreadable(string generationId, string workingRoot) =>
        Catalogue.Format(nameof(SemanticIndexUnreadable), generationId, workingRoot);

    public static string NoSymbolAtPosition => Catalogue.Get(nameof(NoSymbolAtPosition));

    public static string SnapshotMismatch => Catalogue.Get(nameof(SnapshotMismatch));

    public static string OverlayBaseMismatch => Catalogue.Get(nameof(OverlayBaseMismatch));

    public static string ModelNotCalibrated(string model) =>
        Catalogue.Format(nameof(ModelNotCalibrated), model);

    public static string IndexPredatesSnapshotIds(string workingRoot) =>
        Catalogue.Format(nameof(IndexPredatesSnapshotIds), workingRoot);

    public static string IndexBelongsToAnotherRepository(string workingRoot) =>
        Catalogue.Format(nameof(IndexBelongsToAnotherRepository), workingRoot);

    public static string OverlayMissing(string workingRoot) =>
        Catalogue.Format(nameof(OverlayMissing), workingRoot);

    public static string IndexNotBuilt(string indexPath) =>
        Catalogue.Format(nameof(IndexNotBuilt), indexPath);

    public static string ChunkIdMalformed => Catalogue.Get(nameof(ChunkIdMalformed));

    public static string ChunkIdTampered => Catalogue.Get(nameof(ChunkIdTampered));

    public static string ChunkWrongRepository => Catalogue.Get(nameof(ChunkWrongRepository));

    public static string ChunkStaleGeneration => Catalogue.Get(nameof(ChunkStaleGeneration));

    public static string ChunkStaleWorktree => Catalogue.Get(nameof(ChunkStaleWorktree));

    public static string ChunkStaleOverlay => Catalogue.Get(nameof(ChunkStaleOverlay));

    public static string ChunkOutOfRange => Catalogue.Get(nameof(ChunkOutOfRange));

    public static string ChunkSourceStaleContent => Catalogue.Get(nameof(ChunkSourceStaleContent));

    public static string ChunkSourceStaleRange => Catalogue.Get(nameof(ChunkSourceStaleRange));

    public static string ChunkPathOutsideRoot => Catalogue.Get(nameof(ChunkPathOutsideRoot));

    public static string ChunkPathReparsePoint => Catalogue.Get(nameof(ChunkPathReparsePoint));

    public static string ChunkSourceMissing => Catalogue.Get(nameof(ChunkSourceMissing));

    public static string ChunkSourceUnavailable => Catalogue.Get(nameof(ChunkSourceUnavailable));
}
