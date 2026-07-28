using LocalAi.Contracts;

namespace LocalAi.Repository;

public static class RepositoryStateMachine
{
    public static RepositoryIndexState Resolve(
        bool configured,
        RepositoryIndexState persistedState,
        string? publishedGitTree,
        string requestedGitTree,
        bool isDirty,
        bool dirtyOverlayCurrent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedGitTree);
        if (!configured)
        {
            return RepositoryIndexState.NotConfigured;
        }

        if (persistedState is RepositoryIndexState.Initializing or
            RepositoryIndexState.Updating or
            RepositoryIndexState.Failed)
        {
            return persistedState;
        }

        if (string.IsNullOrWhiteSpace(publishedGitTree) ||
            !string.Equals(
                publishedGitTree,
                requestedGitTree,
                StringComparison.Ordinal))
        {
            return RepositoryIndexState.Stale;
        }

        if (!isDirty)
        {
            return RepositoryIndexState.Current;
        }

        return dirtyOverlayCurrent
            ? RepositoryIndexState.DirtyCurrent
            : RepositoryIndexState.DirtyPending;
    }
}
