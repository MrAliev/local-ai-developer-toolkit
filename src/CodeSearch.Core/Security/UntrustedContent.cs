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
    private const int MaxNonceAttempts = 10;
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
        string? nonce = null;
        for (var attempt = 0; attempt < MaxNonceAttempts; attempt++)
        {
            nonceSource.Fill(nonceBytes);
            var candidate = Convert.ToHexStringLower(nonceBytes);
            if (!content.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                nonce = candidate;
                break;
            }
        }

        if (nonce is null)
        {
            throw new InvalidOperationException(
                $"Could not generate a {NonceByteCount * 8}-bit nonce absent from " +
                $"content after {MaxNonceAttempts} attempts.");
        }

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
            switch (character)
            {
                case '&':
                    escaped.Append("&amp;");
                    break;
                case '<':
                    escaped.Append("&lt;");
                    break;
                case '>':
                    escaped.Append("&gt;");
                    break;
                case '"':
                    escaped.Append("&quot;");
                    break;
                case '\'':
                    escaped.Append("&#39;");
                    break;
                case '\r':
                    escaped.Append("&#13;");
                    break;
                case '\n':
                    escaped.Append("&#10;");
                    break;
                case '\t':
                    escaped.Append("&#9;");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        escaped.Append("&#")
                            .Append((int)character)
                            .Append(';');
                    }
                    else
                    {
                        escaped.Append(character);
                    }

                    break;
            }
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
