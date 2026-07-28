using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationIdentity(
    string RepositoryId,
    string DevCommit,
    string DevTree,
    string EmbeddingModel,
    int EmbeddingDimension,
    int ChunkFormatVersion,
    int IndexFormatVersion,
    int NormalizationVersion,
    int RankingVersion)
{
    public bool CanReuseCorpusFrom(GenerationIdentity previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        return string.Equals(RepositoryId, previous.RepositoryId, StringComparison.Ordinal) &&
               string.Equals(
                   EmbeddingModel,
                   previous.EmbeddingModel,
                   StringComparison.Ordinal) &&
               EmbeddingDimension == previous.EmbeddingDimension &&
               ChunkFormatVersion == previous.ChunkFormatVersion &&
               IndexFormatVersion == previous.IndexFormatVersion &&
               NormalizationVersion == previous.NormalizationVersion;
    }

    public string Id
    {
        get
        {
            var value = string.Join(
                "\n",
                RepositoryId,
                DevCommit,
                DevTree,
                EmbeddingModel,
                EmbeddingDimension,
                ChunkFormatVersion,
                IndexFormatVersion,
                NormalizationVersion,
                RankingVersion);
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationManifest(
    GenerationIdentity Identity,
    string IndexFile,
    string IndexChecksum,
    DateTimeOffset PublishedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationPointer(
    string GenerationId,
    string DevTree,
    DateTimeOffset UpdatedAtUtc);

public sealed record OverlayReadiness(
    string Worktree,
    string GenerationId,
    bool Ready);
