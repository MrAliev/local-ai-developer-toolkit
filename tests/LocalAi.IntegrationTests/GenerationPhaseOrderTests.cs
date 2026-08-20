namespace LocalAi.IntegrationTests;

/// <summary>
/// The semantic phase runs before embedding, and this reads the source to say so.
/// </summary>
/// <remarks>
/// A source-level assertion is the unusual choice, taken because the invariant has no runtime
/// seam: `BuildGenerationAsync` needs a broker, a Roslyn workspace and the external SCIP
/// indexers, so a test that observes the real order is an integration run of the whole build.
/// The order is worth pinning anyway. `EnsureSemanticAdaptersSucceeded` aborts the generation
/// when an adapter fails, so running semantics second means paying for the entire corpus —
/// tens of minutes — before discovering the build cannot be published. Swapping it back would
/// look like a harmless tidy-up in review, and nothing else would object.
/// </remarks>
public sealed class GenerationPhaseOrderTests
{
    [Fact]
    public void The_semantic_phase_is_built_before_the_corpus_is_embedded()
    {
        var source = ReadSyncCommandSource();
        var method = source.IndexOf(
            "private static async Task<GenerationManifest> BuildGenerationAsync(",
            StringComparison.Ordinal);
        Assert.True(method >= 0, "BuildGenerationAsync was renamed; this test needs updating.");

        var semantic = source.IndexOf("BuildSemanticIndexAsync(", method, StringComparison.Ordinal);
        var embedding = source.IndexOf("builder.BuildAsync(", method, StringComparison.Ordinal);
        Assert.True(semantic >= 0 && embedding >= 0, "Both phases must still be in the method.");
        Assert.True(
            semantic < embedding,
            "The semantic index must be built before the corpus is embedded. A failed adapter " +
            "aborts the generation, and doing it the other way round spends the whole embedding " +
            "budget before finding that out.");
    }

    [Fact]
    public void Adapter_failure_is_checked_before_the_corpus_is_embedded()
    {
        var source = ReadSyncCommandSource();
        var method = source.IndexOf(
            "private static async Task<GenerationManifest> BuildGenerationAsync(",
            StringComparison.Ordinal);
        var check = source.IndexOf(
            "EnsureSemanticAdaptersSucceeded(",
            method,
            StringComparison.Ordinal);
        var embedding = source.IndexOf("builder.BuildAsync(", method, StringComparison.Ordinal);

        Assert.True(check >= 0 && embedding >= 0);
        Assert.True(
            check < embedding,
            "Aborting on a failed adapter is the point of running semantics first.");
    }

    [Fact]
    public void The_semantic_overlay_is_built_before_the_corpus_overlay()
    {
        // Same invariant one level down. A file changed on a branch has to be cut on the same
        // boundaries as the same file in the base generation, and the chunker can only do that
        // if the worktree's definitions exist before its corpus is embedded. Built the other way
        // round, the shape of a hit would depend on whether the file was in an overlay.
        var source = ReadSyncCommandSource();
        var semantic = source.IndexOf(
            "RepositoryIndexProgressPhase.SemanticOverlay",
            StringComparison.Ordinal);
        var embedding = source.IndexOf("builder.BuildOverlayAsync(", StringComparison.Ordinal);

        Assert.True(semantic >= 0 && embedding >= 0, "Both overlay phases must still be here.");
        Assert.True(
            semantic < embedding,
            "The semantic overlay must be built before the corpus overlay, or a branch " +
            "re-chunks by line window everything it touched.");
    }

    private static string ReadSyncCommandSource()
    {
        // The test assembly's own location walks up to the repository, which is where the source
        // lives; nothing here depends on the working directory a test runner happens to pick.
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(typeof(GenerationPhaseOrderTests).Assembly.Location)!);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(
            directory!.FullName,
            "src",
            "LocalAi.Cli",
            "CodeSearchSyncCommand.cs");
        Assert.True(File.Exists(path), $"Expected the sync command source at '{path}'.");
        return File.ReadAllText(path);
    }
}
