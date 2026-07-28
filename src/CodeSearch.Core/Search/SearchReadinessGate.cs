using System.Collections.Concurrent;
using LocalAi.Contracts;

namespace CodeSearch.Core.Search;

public sealed record SearchReadiness(
    RepositoryIndexState State,
    string RepositoryId,
    string GenerationId,
    string GitTree,
    string? DirtyHash,
    string Model,
    int Dimension,
    int ChunkFormatVersion,
    int IndexFormatVersion);

public sealed record SearchRequirement(
    string RepositoryId,
    string GenerationId,
    string GitTree,
    string? DirtyHash,
    string Model,
    int Dimension,
    int ChunkFormatVersion,
    int IndexFormatVersion);

public sealed class SearchReadinessGate
{
    private readonly ConcurrentDictionary<string, Task<SearchReadiness>> _repairs =
        new(StringComparer.Ordinal);

    public async Task<SearchReadiness> EnsureAsync(
        SearchReadiness current,
        SearchRequirement requirement,
        Func<CancellationToken, Task<SearchReadiness>> repair,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(repair);
        if (IsReady(current, requirement))
        {
            return current;
        }

        if (current.State == RepositoryIndexState.Failed)
        {
            throw Failed(current);
        }

        var key = string.Join(
            ":",
            requirement.RepositoryId,
            requirement.GenerationId,
            requirement.GitTree,
            requirement.DirtyHash ?? "clean");
        var task = _repairs.GetOrAdd(
            key,
            _ => repair(CancellationToken.None));
        SearchReadiness repaired;
        try
        {
            repaired = await task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (task.IsCompleted)
            {
                _repairs.TryRemove(
                    new KeyValuePair<string, Task<SearchReadiness>>(key, task));
            }
        }

        if (!IsReady(repaired, requirement))
        {
            throw repaired.State == RepositoryIndexState.Failed
                ? Failed(repaired)
                : new SearchNotReadyException(
                    $"Semantic search remains {repaired.State}; no stale index was used.");
        }

        return repaired;
    }

    public static bool IsReady(
        SearchReadiness actual,
        SearchRequirement required) =>
        actual.State is RepositoryIndexState.Current or
            RepositoryIndexState.DirtyCurrent &&
        string.Equals(actual.RepositoryId, required.RepositoryId, StringComparison.Ordinal) &&
        string.Equals(actual.GenerationId, required.GenerationId, StringComparison.Ordinal) &&
        string.Equals(actual.GitTree, required.GitTree, StringComparison.Ordinal) &&
        string.Equals(actual.DirtyHash, required.DirtyHash, StringComparison.Ordinal) &&
        string.Equals(actual.Model, required.Model, StringComparison.Ordinal) &&
        actual.Dimension == required.Dimension &&
        actual.ChunkFormatVersion == required.ChunkFormatVersion &&
        actual.IndexFormatVersion == required.IndexFormatVersion;

    private static SearchNotReadyException Failed(SearchReadiness state) =>
        new(
            $"Semantic search is Failed for generation '{state.GenerationId}'. " +
            "Choose one fallback: diagnose/restart MCP; use the LocalAi CLI through " +
            "the broker; or continue without local models using rg.");
}

public sealed class SearchNotReadyException(string message)
    : InvalidOperationException(message);
