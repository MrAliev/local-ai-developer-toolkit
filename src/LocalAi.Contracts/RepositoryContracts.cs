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

/// <summary>
/// Where an index build currently is.
///
/// Embedding is the loudest phase but not the only slow one. A generation also has to build its
/// semantic graph — Roslyn loads the solution, the SCIP adapters run — and then copy the corpus
/// into place and checksum it. Those took two and a half minutes on a real repository while the
/// last reported phase was still <see cref="EmbeddingBase"/> at its final chunk, which reads as a
/// finished build that is refusing to publish. Every phase that can take minutes now says so.
/// </summary>
[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum RepositoryIndexProgressPhase
{
    Planning,
    EmbeddingBase,
    SemanticBase,
    PublishingGeneration,
    EmbeddingOverlay,
    SemanticOverlay,
    Publishing,
    Completed,
    Failed
}

public static class RepositoryIndexProgressPhaseExtensions
{
    /// <summary>
    /// Whether the phase's chunk counters mean anything. Outside embedding they are the frozen
    /// tally of the last phase that had one, and printing "1415/1415, 0 remaining" beside a phase
    /// that is not counting chunks is how a build in progress comes to look like a finished one.
    /// </summary>
    public static bool CountsChunks(this RepositoryIndexProgressPhase phase) =>
        phase is RepositoryIndexProgressPhase.EmbeddingBase
            or RepositoryIndexProgressPhase.EmbeddingOverlay;
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
