using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// The estimate a bounded caller decides on, and the only one available before the semantic
/// phase — chunk counts do not exist that early, because C# is cut on the definitions that
/// phase produces (#275).
/// </summary>
public sealed class ChangedFileCountTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-changed-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void With_no_index_to_compare_against_every_file_is_changed()
    {
        Sources(3);

        Assert.Equal(3, IndexBuilder.CountChangedFiles(_root, againstIndexPath: null));
    }

    [Fact]
    public async Task Against_an_index_only_what_differs_is_counted()
    {
        Sources(3);
        var index = Path.Combine(_root, "base.cidx");
        await new IndexBuilder(new StubEmbedder()).BuildAsync(
            _root,
            index,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, IndexBuilder.CountChangedFiles(_root, index));

        File.WriteAllText(Path.Combine(_root, "File1.cs"), "public sealed class File1 { }");
        File.WriteAllText(Path.Combine(_root, "Added.cs"), "public sealed class Added { }");

        Assert.Equal(2, IndexBuilder.CountChangedFiles(_root, index));
    }

    /// <summary>
    /// A path naming no index is the cold-build case, and a cold build is the largest work
    /// there is — answering zero there would let exactly the case #275 is about run inline.
    /// </summary>
    [Fact]
    public void An_index_that_is_not_there_is_not_a_reason_to_answer_zero()
    {
        Sources(2);

        Assert.Equal(
            2,
            IndexBuilder.CountChangedFiles(_root, Path.Combine(_root, "absent.cidx")));
    }

    private void Sources(int count)
    {
        Directory.CreateDirectory(_root);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(
                Path.Combine(_root, $"File{index}.cs"),
                $"public sealed class File{index} {{ public int Value => {index}; }}");
        }
    }

    private sealed class StubEmbedder : IEmbeddingClient
    {
        public string Model => "test-model";

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
