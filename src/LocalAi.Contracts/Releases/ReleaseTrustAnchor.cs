using System.Reflection;

namespace LocalAi.Contracts;

/// <summary>
/// The single release signing key this product trusts.
///
/// The key is embedded rather than read from disk so that a release cannot be verified
/// against a key an attacker dropped next to the executable, and so that every component
/// stays self-contained on a machine that has never seen this repository.
///
/// It lives beside the manifest format rather than inside the installer because installing a
/// release is not the only thing that has to tell a real one from whoever answered the
/// request: the runtime asks the same question when it looks up whether a newer release
/// exists, and two copies of a trust anchor is one more than a product can have.
/// </summary>
public static class ReleaseTrustAnchor
{
    private const string ResourceName = "LocalAi.release-signing-public.spki.der";

    private static readonly byte[] PublicKeyBytes = LoadPublicKey();

    /// <summary>
    /// SubjectPublicKeyInfo bytes of the trusted ECDSA P-256 release key. A copy is
    /// returned so a caller cannot mutate the trust anchor for everyone else.
    /// </summary>
    public static byte[] PublicKey => (byte[])PublicKeyBytes.Clone();

    public static ReleaseManifestVerifier CreateManifestVerifier() => new(PublicKeyBytes);

    private static byte[] LoadPublicKey()
    {
        using var stream = typeof(ReleaseTrustAnchor).Assembly
                .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The release signing key resource '{ResourceName}' is missing from the build.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException(
                "The embedded release signing key is empty.");
        }

        return bytes;
    }
}
