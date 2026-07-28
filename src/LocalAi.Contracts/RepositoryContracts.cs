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
