using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CodeSearch.Core.Semantics;
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
    int RankingVersion,
    int SemanticIndexVersion = 0)
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
            var values = new List<object>
            {
                RepositoryId,
                DevCommit,
                DevTree,
                EmbeddingModel,
                EmbeddingDimension,
                ChunkFormatVersion,
                IndexFormatVersion,
                NormalizationVersion,
                RankingVersion,
            };
            // Preserve IDs of all pre-SIDX generations. Once semantic data participates in a
            // generation, its format version becomes part of the immutable identity.
            if (SemanticIndexVersion > 0)
            {
                values.Add(SemanticIndexVersion);
            }

            var value = string.Join("\n", values);
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
    DateTimeOffset PublishedAtUtc,
    string? SemanticIndexFile = null,
    string? SemanticIndexChecksum = null,
    IReadOnlyList<SemanticAdapterStatus>? SemanticAdapters = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationPointer(
    string GenerationId,
    string DevTree,
    DateTimeOffset UpdatedAtUtc);

public sealed record OverlayReadiness(
    string Worktree,
    string GenerationId,
    bool Ready);
