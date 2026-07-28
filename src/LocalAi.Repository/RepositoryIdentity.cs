using System.Security.Cryptography;
using System.Text;

namespace LocalAi.Repository;

public sealed record RepositoryIdentity(string Id, string CommonDirectory)
{
    public static RepositoryIdentity FromCommonDirectory(string commonDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonDirectory);
        var normalized = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(commonDirectory));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return new RepositoryIdentity(
            Convert.ToHexString(hash).ToLowerInvariant(),
            normalized);
    }
}
