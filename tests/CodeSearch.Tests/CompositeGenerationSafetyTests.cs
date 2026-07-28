using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

public sealed class CompositeGenerationSafetyTests
{
    [Fact]
    public void Overlay_from_other_base_is_rejected()
    {
        var baseIndex = Index("base-commit", baseCommit: string.Empty);
        var overlay = Index("branch-commit", baseCommit: "other-base");

        var error = Assert.Throws<InvalidOperationException>(
            () => new CompositeIndex(baseIndex, overlay));

        Assert.Contains("does not match generation base", error.Message);
    }

    [Fact]
    public void Overlay_from_other_generation_is_rejected()
    {
        var baseIndex = Index(
            "base-commit",
            string.Empty,
            repositoryId: "repository",
            generationId: "generation-a");
        var overlay = Index(
            "branch-commit",
            "base-commit",
            repositoryId: "repository",
            generationId: "generation-b");

        Assert.Throws<InvalidOperationException>(
            () => new CompositeIndex(baseIndex, overlay));
    }

    private static CodeIndex Index(
        string commit,
        string baseCommit,
        string repositoryId = "",
        string generationId = "") => new()
    {
        Dim = 2,
        Model = "model",
        Root = @"C:\repo",
        GitCommit = commit,
        IndexedAtUtc = DateTime.UtcNow,
        Files = [],
        Chunks = [],
        Vectors = [],
        BaseCommit = baseCommit,
        RepositoryId = repositoryId,
        GenerationId = generationId
    };
}
