using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Indexing;

internal static class CanonicalIndexText
{
    public static string Read(string path) => Normalize(File.ReadAllText(path));

    public static string Normalize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    public static byte[] Hash(string content) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(content)));

    public static byte[] Bytes(string content) =>
        Encoding.UTF8.GetBytes(Normalize(content));
}
