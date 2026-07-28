using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Chunking;

namespace CodeSearch.Core.Indexing;

public static class DirtyCorpusPolicy
{
    private static readonly HashSet<string> ExcludedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "bin", "obj", "node_modules", "packages", "dist",
            "build", "out", "artifacts", "TestResults", "coverage"
        };

    private static readonly string[] SensitiveFragments =
    [
        ".env",
        "credential",
        "credentials",
        "secret",
        "token",
        "private-key",
        "private_key"
    ];

    private static readonly HashSet<string> SensitiveExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".key", ".pem", ".pfx", ".p12", ".cer", ".crt", ".kdbx"
        };

    public static bool IsAllowed(string relativePath, bool gitIgnored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (gitIgnored)
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(ExcludedDirectories.Contains))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalized);
        if (SensitiveExtensions.Contains(Path.GetExtension(fileName)) ||
            SensitiveFragments.Any(fragment =>
                fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return ChunkerFactory.IsIndexable(normalized);
    }

    public static string ComputeContentHash(
        string root,
        IEnumerable<string> relativePaths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in relativePaths
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = relativePath.Replace('\\', '/');
            var content = CanonicalIndexText.Bytes(
                File.ReadAllText(Path.Combine(root, relativePath)));
            hash.AppendData(Encoding.UTF8.GetBytes(normalized));
            hash.AppendData([0]);
            hash.AppendData(content);
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeWorkingContentHash(
        string root,
        IEnumerable<string> relativePaths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in relativePaths
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = relativePath.Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(normalized));
            hash.AppendData([0]);
            var path = Path.Combine(root, relativePath);
            if (File.Exists(path))
            {
                hash.AppendData(CanonicalIndexText.Bytes(File.ReadAllText(path)));
            }
            else
            {
                hash.AppendData(Encoding.UTF8.GetBytes("<deleted>"));
            }

            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
