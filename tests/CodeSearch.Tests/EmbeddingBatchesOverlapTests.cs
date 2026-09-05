using System.Text.RegularExpressions;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// The broker keeps a model resident while the queue holds work for it and unloads it the moment
/// the queue is empty. A build that posts the next batch only after the previous one has answered
/// leaves the queue empty between every two batches, so every batch paid for a model load —
/// about a third of a sync's wall time. Keeping one batch in flight is what closes the gap.
///
/// The fake embedder answers synchronously, so the order in which posts and consumptions
/// interleave is a property of the build loop alone, not of timing.
/// </summary>
public sealed class EmbeddingBatchesOverlapTests : IDisposable
{
    private const int FileCount = 40;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-batches-overlap-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_next_batch_is_posted_before_the_last_one_is_consumed()
    {
        var events = new List<string>();
        var embedder = new EncodingEmbedder(events);

        var result = await new IndexBuilder(
                embedder,
                progress: item =>
                {
                    if (item.ProcessedChunks > 0)
                    {
                        events.Add("consumed");
                    }
                })
            .BuildAsync(SourceRoot(), IndexPath, ct: TestContext.Current.CancellationToken);

        Assert.True(embedder.Posts >= 3, $"the fixture must need at least three batches, got {embedder.Posts}");
        Assert.Equal(result.ChunksEmbedded, embedder.Inputs.Count);

        // post, post, consumed, post, consumed, …, consumed: every batch but the first is on
        // the queue before the one ahead of it is taken off, and never more than one waits.
        var expected = new List<string> { "post" };
        for (var batch = 1; batch < embedder.Posts; batch++)
        {
            expected.Add("post");
            expected.Add("consumed");
        }

        expected.Add("consumed");
        Assert.Equal(expected, events);
    }

    /// <summary>
    /// Guards the loop rewrite rather than the behaviour above: with two batches in the air, the
    /// answer to one must not be written into the slots of the other.
    /// </summary>
    [Fact]
    public async Task Every_vector_lands_in_the_slot_of_the_chunk_it_was_computed_for()
    {
        await new IndexBuilder(new EncodingEmbedder([]))
            .BuildAsync(SourceRoot(), IndexPath, ct: TestContext.Current.CancellationToken);

        AssertEveryVectorIsItsOwn();
    }

    private void AssertEveryVectorIsItsOwn()
    {
        var index = CodeIndex.Load(IndexPath);
        Assert.Equal(FileCount, index.Files.Count);
        Assert.All(index.Files, file =>
        {
            var expected = EncodingEmbedder.Encode(Number(file.RelPath));
            var actual = index.VectorAt(file.ChunkStart).ToArray();
            Assert.Equal(expected[0], actual[0], 1e-5f);
            Assert.Equal(expected[1], actual[1], 1e-5f);
        });
    }

    /// <summary>
    /// Posting ahead is only worth having if a failure stops it. The first draft did not check,
    /// and the checkpoint test caught it: one batch went to the broker behind a batch that had
    /// already failed, was never consumed, never reached the checkpoint, and was embedded again
    /// on the resumed build — 55 chunks charged for 40.
    /// </summary>
    [Fact]
    public async Task Nothing_is_posted_behind_a_batch_that_has_already_failed()
    {
        var embedder = new FailsOnSecondBatchEmbedder();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new IndexBuilder(embedder).BuildAsync(
                SourceRoot(),
                IndexPath,
                ct: TestContext.Current.CancellationToken));

        // The one that failed, and the one posted before its failure was visible. Not a third.
        Assert.Equal(2, embedder.Posts);
    }

    /// <summary>
    /// With two batches in the air the broker may answer the second first — a shorter batch, or
    /// one whose halves were retried. The pairing of a result with its batch must not depend on
    /// which answer arrives first.
    /// </summary>
    [Fact]
    public async Task A_batch_that_answers_before_the_one_ahead_of_it_still_lands_in_its_own_slots()
    {
        var embedder = new AnswersTheNewestFirstEmbedder(FileCount);

        await new IndexBuilder(embedder).BuildAsync(
            SourceRoot(),
            IndexPath,
            ct: TestContext.Current.CancellationToken);

        Assert.True(
            embedder.CompletionOrder.Count >= 2 &&
            embedder.CompletionOrder[0] > embedder.CompletionOrder[1],
            "the fake must have answered a later batch before the one ahead of it, got " +
            string.Join(", ", embedder.CompletionOrder));
        AssertEveryVectorIsItsOwn();
    }

    private string IndexPath => Path.Combine(_root, "index.cidx");

    private string SourceRoot()
    {
        var sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        for (var index = 0; index < FileCount; index++)
        {
            File.WriteAllText(
                Path.Combine(sourceRoot, $"notes-{index:D2}.md"),
                $"unique-{index:D2}\n" + new string((char)('a' + index % 26), 6_000));
        }

        return sourceRoot;
    }

    private static int Number(string text) =>
        int.Parse(Regex.Match(text, @"(\d\d)").Groups[1].Value);

    /// <summary>
    /// Answers at once, with a vector that says which chunk it was asked about, and records
    /// every post so the test can see where each one fell against the consumptions.
    /// </summary>
    private sealed class EncodingEmbedder(List<string> events) : IEmbeddingClient
    {
        public string Model => "test-model";

        public int Posts { get; private set; }

        public List<string> Inputs { get; } = [];

        public static float[] Encode(int number) =>
            [MathF.Cos(number * 0.1f), MathF.Sin(number * 0.1f)];

        public static float[][] Answer(IReadOnlyList<string> inputs) => inputs
            .Select(input => Encode(Number(Regex.Match(input, @"unique-(\d\d)").Value)))
            .ToArray();

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            Posts++;
            events.Add("post");
            Inputs.AddRange(inputs);
            return Task.FromResult(Answer(inputs));
        }
    }

    /// <summary>
    /// Fails the second batch the way a cancelled run does — the one shape EmbedBatchAsync does
    /// not answer by halving, so the failure reaches the loop instead of being retried away.
    /// </summary>
    private sealed class FailsOnSecondBatchEmbedder : IEmbeddingClient
    {
        public string Model => "test-model";

        public int Posts { get; private set; }

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            Posts++;
            if (Posts == 2)
            {
                throw new OperationCanceledException("Simulated failure.");
            }

            return Task.FromResult(EncodingEmbedder.Answer(inputs));
        }
    }

    /// <summary>
    /// Holds a batch until the next one is posted, then answers the newest first — so a later
    /// batch always completes before the one ahead of it. The total is known, so the last batch
    /// is answered on its own post and nothing is left waiting.
    /// </summary>
    private sealed class AnswersTheNewestFirstEmbedder(int expectedInputs) : IEmbeddingClient
    {
        private readonly List<(int Post, TaskCompletionSource<float[][]> Completion, float[][] Answer)> _held = [];
        private int _posts;
        private int _seen;

        public string Model => "test-model";

        public List<int> CompletionOrder { get; } = [];

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<float[][]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Add((++_posts, completion, EncodingEmbedder.Answer(inputs)));
            _seen += inputs.Count;

            if (_held.Count < 2 && _seen < expectedInputs)
            {
                return completion.Task;
            }

            for (var index = _held.Count - 1; index >= 0; index--)
            {
                var (post, pending, answer) = _held[index];
                CompletionOrder.Add(post);
                pending.SetResult(answer);
            }

            _held.Clear();
            return completion.Task;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
