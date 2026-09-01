using System.Security.Cryptography;
using System.Text;
using LocalAi.Contracts;

namespace LocalAi.Repository;

/// <summary>
/// <paramref name="CommonDirectory"/> is the identity spelling, not a display one: absolute,
/// native separators, and upper-cased on Windows. It is what <paramref name="Id"/> hashes, and
/// changing either renames every index directory on every machine.
/// </summary>
public sealed record RepositoryIdentity(string Id, string CommonDirectory)
{
    /// <summary>
    /// Takes an <see cref="FsPath"/> rather than a string so the normalisation cannot be
    /// skipped by a caller who already had "a path". It used to be repeated here, in
    /// RuntimeIndexLayout.WorktreeKey and in the callers of both — three copies of one rule,
    /// which is how a worktree came to hash differently depending on who asked.
    /// </summary>
    public static RepositoryIdentity FromCommonDirectory(FsPath commonDirectory)
    {
        var key = commonDirectory.IdentityKey;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new RepositoryIdentity(Convert.ToHexString(hash).ToLowerInvariant(), key);
    }

    /// <summary>For a path arriving as text — a command-line argument, git output, a manifest.</summary>
    public static RepositoryIdentity FromCommonDirectory(string commonDirectory) =>
        FromCommonDirectory(FsPath.From(commonDirectory));
}
