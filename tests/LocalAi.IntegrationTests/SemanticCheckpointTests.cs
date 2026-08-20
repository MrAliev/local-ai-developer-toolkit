using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The semantic phase runs before embedding so that a failed adapter costs minutes instead of the
/// whole embedding budget. These pin the other half of that trade: an interrupted build resumes
/// the phase instead of paying for it again, and it resumes only from a file that is actually its
/// own.
/// </summary>
public sealed class SemanticCheckpointTests : IDisposable
{
    private const string RepositoryId = "repository-id";
    private const string Tree = "3c13c77045ef9e4f9e9de13954334815c1657df3";

    private readonly string _staging = Path.Combine(
        Path.GetTempPath(),
        "localai-semantic-checkpoint-" + Guid.NewGuid().ToString("N"));

    public SemanticCheckpointTests() => Directory.CreateDirectory(_staging);

    [Fact]
    public void Resumes_from_a_checkpoint_of_the_same_generation()
    {
        var generation = Generation();
        var checkpoint = Write(generation, Index(generation.Id), Succeeded);

        var resumed = CodeSearchSyncCommand.ResumeSemanticPhase(
            checkpoint,
            checkpoint + ".adapters.json",
            generation,
            Dev());

        Assert.NotNull(resumed);
        Assert.Equal(generation.Id, resumed!.Index.GenerationId);
        Assert.Equal(
            Succeeded.Select(status => (status.Name, status.State)),
            resumed.AdapterStatuses.Select(status => (status.Name, status.State)));
    }

    [Fact]
    public void Rebuilds_when_the_checkpoint_belongs_to_another_generation()
    {
        // The generation id covers the tree, the model, and every format version, so a mismatch
        // means the staged index answers a question this build is not asking.
        var generation = Generation();
        var checkpoint = Write(generation, Index("another-generation"), Succeeded);

        Assert.Null(CodeSearchSyncCommand.ResumeSemanticPhase(
            checkpoint,
            checkpoint + ".adapters.json",
            generation,
            Dev()));
    }

    [Fact]
    public void Rebuilds_when_the_checkpoint_indexes_another_tree()
    {
        var generation = Generation();
        var checkpoint = Write(
            generation,
            Index(generation.Id) with { GitTree = "0000000000000000000000000000000000000000" },
            Succeeded);

        Assert.Null(CodeSearchSyncCommand.ResumeSemanticPhase(
            checkpoint,
            checkpoint + ".adapters.json",
            generation,
            Dev()));
    }

    [Fact]
    public void Rebuilds_when_the_adapter_statuses_are_missing()
    {
        // Half a checkpoint is not a checkpoint: publishing a generation needs the statuses in
        // its manifest, and inventing them would record adapters that were never run.
        var generation = Generation();
        var checkpoint = Write(generation, Index(generation.Id), Succeeded);
        File.Delete(checkpoint + ".adapters.json");

        Assert.Null(CodeSearchSyncCommand.ResumeSemanticPhase(
            checkpoint,
            checkpoint + ".adapters.json",
            generation,
            Dev()));
    }

    [Fact]
    public void Rebuilds_when_the_checkpoint_is_unreadable()
    {
        // A build killed mid-write leaves a file that is not a semantic index. It has to read as
        // "no checkpoint", not as a failure: the phase can always be run again.
        var generation = Generation();
        var checkpoint = Write(generation, Index(generation.Id), Succeeded);
        File.WriteAllBytes(checkpoint, [0x00, 0x01, 0x02, 0x03]);

        Assert.Null(CodeSearchSyncCommand.ResumeSemanticPhase(
            checkpoint,
            checkpoint + ".adapters.json",
            generation,
            Dev()));
    }

    [Fact]
    public void Refuses_a_checkpoint_whose_adapters_failed()
    {
        var generation = Generation();
        var checkpoint = Write(
            generation,
            Index(generation.Id),
            [new SemanticAdapterStatus("typescript", SemanticAdapterState.Failed, "bad shim", 1)]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodeSearchSyncCommand.ResumeSemanticPhase(
                checkpoint,
                checkpoint + ".adapters.json",
                generation,
                Dev()));

        Assert.Contains("typescript: bad shim", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_staging))
        {
            Directory.Delete(_staging, recursive: true);
        }
    }

    private static SemanticAdapterStatus[] Succeeded =>
    [
        new("typescript", SemanticAdapterState.Succeeded, "SCIP output imported.", 1200),
        new("python", SemanticAdapterState.Skipped, "no files", 0),
    ];

    private string Write(
        GenerationIdentity generation,
        SemanticIndex index,
        IReadOnlyList<SemanticAdapterStatus> statuses)
    {
        var path = Path.Combine(_staging, generation.Id + ".sidx");
        index.Save(path);
        CodeSearchSyncCommand.WriteSemanticAdapterStatuses(path + ".adapters.json", statuses);
        return path;
    }

    private static GenerationIdentity Generation() => new(
        RepositoryId,
        "b609fcfdfc4087d04d7ee8a06a12c99ae94ab236",
        Tree,
        CodeSearchSyncCommand.DefaultModel,
        CodeSearchSyncCommand.DefaultDimension,
        CodeSearchSyncCommand.CurrentChunkFormatVersion,
        CodeIndex.CurrentVersion,
        CodeSearchSyncCommand.CurrentNormalizationVersion,
        1,
        CodeSearchSyncCommand.CurrentSemanticGenerationVersion);

    private static WorkingIndexIdentity Dev() => new(
        "R:/Repository",
        "R:/Repository",
        RepositoryId,
        "R:/Runtime",
        "b609fcfdfc4087d04d7ee8a06a12c99ae94ab236",
        Tree,
        null);

    private static SemanticIndex Index(string generationId) => new()
    {
        RepositoryId = RepositoryId,
        GenerationId = generationId,
        GitTree = Tree,
        DirtyHash = null,
        BaseCommit = "b609fcfdfc4087d04d7ee8a06a12c99ae94ab236",
        IndexedAtUtc = DateTime.UnixEpoch,
        Documents = [new SemanticDocument { RelPath = "src/app.ts", Hash = new byte[32] }],
        Symbols = [],
        Occurrences = [],
        Relationships = [],
    };
}
