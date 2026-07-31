using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Security;

public interface IUntrustedContentNonceSource
{
    void Fill(Span<byte> nonce);
}

public static class UntrustedContent
{
    private const int NonceByteCount = 12;
    private static readonly IUntrustedContentNonceSource CryptographicNonceSource =
        new RandomNumberGeneratorNonceSource();

    public static string Wrap(
        string content,
        string origin,
        IUntrustedContentNonceSource? nonceSource = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(origin);

        nonceSource ??= CryptographicNonceSource;
        Span<byte> nonceBytes = stackalloc byte[NonceByteCount];
        string nonce;
        do
        {
            nonceSource.Fill(nonceBytes);
            nonce = Convert.ToHexStringLower(nonceBytes);
        }
        while (content.Contains(nonce, StringComparison.OrdinalIgnoreCase));

        return new StringBuilder(
                content.Length +
                origin.Length +
                (nonce.Length * 2) +
                80)
            .Append("<untrusted-content id=\"")
            .Append(nonce)
            .Append("\" origin=\"")
            .Append(EscapeAttribute(origin))
            .Append("\">\n")
            .Append(content)
            .Append("\n</untrusted-content id=\"")
            .Append(nonce)
            .Append("\">")
            .ToString();
    }

    private static string EscapeAttribute(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            escaped.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                '\r' => "&#13;",
                '\n' => "&#10;",
                '\t' => "&#9;",
                _ => character.ToString()
            });
        }

        return escaped.ToString();
    }

    private sealed class RandomNumberGeneratorNonceSource
        : IUntrustedContentNonceSource
    {
        public void Fill(Span<byte> nonce) =>
            RandomNumberGenerator.Fill(nonce);
    }
}
