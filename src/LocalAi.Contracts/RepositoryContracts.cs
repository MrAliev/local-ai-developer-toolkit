using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum RepositoryIndexState
{
    NotConfigured,
    Initializing,
    Current,
    DirtyPending,
    DirtyCurrent,
    Updating,
    Stale,
    Failed
}

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum RepositoryIndexProgressPhase
{
    Planning,
    EmbeddingBase,
    EmbeddingOverlay,
    Publishing,
    Completed,
    Failed
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepositoryIndexProgress(
    string RepositoryId,
    RepositoryIndexProgressPhase Phase,
    string WorkingRoot,
    int ProcessedChunks,
    int TotalChunks,
    double ChunksPerSecond,
    TimeSpan? EstimatedRemaining,
    DateTimeOffset UpdatedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepositoryWorktree(
    string Path,
    string Head,
    string? Branch);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepositoryManifest(
    string RepositoryId,
    string CommonDirectory,
    string DevRef,
    string? CurrentGenerationId,
    string? PublishedGitTree,
    string EmbeddingModel,
    int EmbeddingDimension,
    int ChunkFormatVersion,
    int IndexFormatVersion,
    RepositoryIndexState State,
    IReadOnlyList<RepositoryWorktree> ActiveWorktrees,
    DateTimeOffset UpdatedAtUtc);
