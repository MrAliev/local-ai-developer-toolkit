using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class EmbeddingCheckpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-embedding-checkpoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Completed_batches_are_reused_after_interrupted_build()
    {
        var sourceRoot = Path.Combine(_root, "source");
        var indexPath = Path.Combine(_root, "index.cidx");
        var checkpointPath = Path.Combine(_root, "checkpoint");
        Directory.CreateDirectory(sourceRoot);
        for (var index = 0; index < 40; index++)
        {
            File.WriteAllText(
                Path.Combine(sourceRoot, $"notes-{index:D2}.md"),
                $"unique-{index:D2}\n" + new string((char)('a' + index % 26), 6_000));
        }

        var interrupted = new InterruptOnSecondBatchEmbedder();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new IndexBuilder(interrupted).BuildAsync(
                sourceRoot,
                indexPath,
                ct: TestContext.Current.CancellationToken,
                embeddingCheckpointPath: checkpointPath,
                expectedEmbeddingDimension: 2));

        Assert.NotEmpty(interrupted.CompletedInputs);
        Assert.NotEmpty(Directory.EnumerateFiles(checkpointPath, "*.batch"));

        var progress = new List<IndexBuildProgress>();
        var resumed = new RecordingEmbedder();
        var result = await new IndexBuilder(resumed, progress: progress.Add).BuildAsync(
            sourceRoot,
            indexPath,
            ct: TestContext.Current.CancellationToken,
            embeddingCheckpointPath: checkpointPath,
            expectedEmbeddingDimension: 2);

        Assert.Equal(
            result.ChunksEmbedded,
            interrupted.CompletedInputs.Count + resumed.Inputs.Count);
        Assert.Empty(interrupted.CompletedInputs.Intersect(resumed.Inputs, StringComparer.Ordinal));
        Assert.Contains(
            progress,
            item => item.ProcessedChunks == interrupted.CompletedInputs.Count &&
                    item.TotalChunks == result.ChunksEmbedded);
        Assert.Equal(result.ChunkCount, CodeIndex.Load(indexPath).Chunks.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class InterruptOnSecondBatchEmbedder : IEmbeddingClient
    {
        private int _calls;

        public string Model => "test-model";

        public List<string> CompletedInputs { get; } = [];

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 2)
            {
                throw new OperationCanceledException("Simulated interruption.");
            }

            CompletedInputs.AddRange(inputs);
            return Task.FromResult(
                inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }

    private sealed class RecordingEmbedder : IEmbeddingClient
    {
        public string Model => "test-model";

        public List<string> Inputs { get; } = [];

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            Inputs.AddRange(inputs);
            return Task.FromResult(
                inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }
}
