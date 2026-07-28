namespace LocalAi.Contracts;

public sealed record IndexContext
{
    public IndexContext(
        string repositoryId,
        string generationId,
        string gitTree)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitTree);

        RepositoryId = repositoryId;
        GenerationId = generationId;
        GitTree = gitTree;
    }

    public string RepositoryId { get; }

    public string GenerationId { get; }

    public string GitTree { get; }
}
