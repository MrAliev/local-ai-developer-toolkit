using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class TranslationChunkerTests
{
    [Fact]
    public void Chunks_stay_under_translategemma_limit_and_reassemble_exactly()
    {
        var source = string.Join(
            "\r\n\r\n",
            Enumerable.Range(1, 40).Select(index =>
                $"## Section {index}\r\n" + new string((char)('a' + index % 20), 220)));

        var chunks = TranslationChunker.Chunk(source, maxCharacters: 1200);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 1200));
        Assert.Equal(source, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Equal(
            Enumerable.Range(0, chunks.Count),
            chunks.Select(chunk => chunk.Index));
    }
}
