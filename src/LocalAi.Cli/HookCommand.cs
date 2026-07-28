namespace LocalAi.Cli;

public enum RepositoryHookEvent
{
    PostCommit,
    PostMerge,
    PostRewrite,
    PostCheckout,
    ReferenceTransaction
}

public enum HookDispatchMode
{
    Synchronous,
    Queued
}

public sealed record HookDispatchPlan(
    RepositoryHookEvent Event,
    string TargetTree,
    int EstimatedChunks,
    HookDispatchMode Mode,
    string DeduplicationKey);

public static class HookCommand
{
    public const int SynchronousChunkLimit = 24;

    public static HookDispatchPlan Plan(
        RepositoryHookEvent hookEvent,
        string repositoryId,
        string targetTree,
        int estimatedChunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTree);
        if (estimatedChunks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedChunks));
        }

        return new HookDispatchPlan(
            hookEvent,
            targetTree,
            estimatedChunks,
            estimatedChunks <= SynchronousChunkLimit
                ? HookDispatchMode.Synchronous
                : HookDispatchMode.Queued,
            $"repository:{repositoryId}:tree:{targetTree}");
    }
}
