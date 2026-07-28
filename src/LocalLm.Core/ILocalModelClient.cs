using LocalAi.Contracts;

namespace LocalLm.Core;

public interface ILocalModelClient
{
    Task<LocalJobResult<string>> ChatAsync(
        string model,
        string prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64,
        LocalJobPriority priority,
        CancellationToken cancellationToken = default);

    Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
        CancellationToken cancellationToken = default);
}
