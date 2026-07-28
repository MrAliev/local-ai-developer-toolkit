using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Indexing;

public enum OverlayKind
{
    Commit,
    Collapsed,
    Dirty
}

public sealed record OverlayIdentity(
    string GenerationId,
    string BaseTree,
    string TargetTree,
    string? TargetCommit,
    OverlayKind Kind,
    string? ContentHash)
{
    public string Id
    {
        get
        {
            var value = string.Join(
                "\n",
                GenerationId,
                BaseTree,
                TargetTree,
                TargetCommit ?? string.Empty,
                Kind,
                ContentHash ?? string.Empty);
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();
        }
    }
}

public sealed record CommitNode(
    string Commit,
    string Tree,
    string? FirstParentCommit,
    string? FirstParentTree);
