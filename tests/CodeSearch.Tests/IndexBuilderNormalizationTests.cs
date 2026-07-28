using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class IndexBuilderNormalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-normalization-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Equivalent_lf_and_crlf_content_produces_an_empty_overlay()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "notes.md");
        var basePath = Path.Combine(_root, "base.cidx");
        var overlayPath = Path.Combine(_root, "overlay.cidx");
        File.WriteAllText(sourcePath, "line one\nline two\n");
        var embedder = new RecordingEmbedder();
        var builder = new IndexBuilder(embedder);
        await builder.BuildAsync(
            _root,
            basePath,
            ct: TestContext.Current.CancellationToken);

        Assert.All(embedder.Inputs, input => Assert.True(HasOnlyCrlf(input)));
        embedder.Inputs.Clear();
        File.WriteAllText(sourcePath, "line one\r\nline two\r\n");

        var result = await builder.BuildOverlayAsync(
            _root,
            basePath,
            overlayPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.FilesEmbedded);
        Assert.Empty(embedder.Inputs);
    }

    [Fact]
    public async Task A_real_text_edit_still_produces_an_overlay_entry()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "notes.md");
        var basePath = Path.Combine(_root, "base.cidx");
        var overlayPath = Path.Combine(_root, "overlay.cidx");
        File.WriteAllText(sourcePath, "line one\nline two\n");
        var builder = new IndexBuilder(new RecordingEmbedder());
        await builder.BuildAsync(
            _root,
            basePath,
            ct: TestContext.Current.CancellationToken);
        File.WriteAllText(sourcePath, "line one\r\nchanged\r\n");

        var result = await builder.BuildOverlayAsync(
            _root,
            basePath,
            overlayPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(1, result.FilesEmbedded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static bool HasOnlyCrlf(string value)
    {
        var withoutCrlf = value.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        return !withoutCrlf.Contains('\r') && !withoutCrlf.Contains('\n');
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
