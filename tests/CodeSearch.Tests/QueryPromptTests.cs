using CodeSearch.Core.Embedding;

namespace CodeSearch.Tests;

public class QueryPromptTests
{
    [Theory]
    [InlineData("qwen3-embedding:4b")]
    [InlineData("qwen3-embedding:8b-fp16")]
    [InlineData("QWEN3-EMBEDDING:0.6b")]
    public void WrapsQueriesForModelsTrainedWithInstructions(string model)
    {
        var prompt = QueryPrompt.ForQuery(model, "closing an order after payment");

        Assert.StartsWith("Instruct: ", prompt);
        Assert.Contains("\nQuery: closing an order after payment", prompt);
    }

    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("mxbai-embed-large")]
    public void LeavesOtherModelsAlone(string model)
    {
        // A model that never saw "Instruct:" during training just gets extra noise in its vector.
        Assert.Equal("closing an order", QueryPrompt.ForQuery(model, "closing an order"));
    }

    [Fact]
    public void DocumentsAreNeverWrapped()
    {
        // Asymmetric by design: only the query side carries the instruction, which is why turning
        // this on does not invalidate an existing index.
        Assert.True(QueryPrompt.UsesInstructions("qwen3-embedding:8b-fp16"));
        Assert.DoesNotContain("Instruct:", QueryPrompt.ForQuery("nomic-embed-text", "public class Order"));
    }
}
