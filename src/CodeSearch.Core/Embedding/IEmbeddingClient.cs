using LocalAi.Contracts;

namespace CodeSearch.Core.Embedding;

public interface IEmbeddingClient
{
    string Model { get; }

    Task<float[][]> EmbedAsync(
        IReadOnlyList<string> inputs,
        LocalJobPriority priority,
        string deduplicationKey,
        CancellationToken cancellationToken = default);
}
