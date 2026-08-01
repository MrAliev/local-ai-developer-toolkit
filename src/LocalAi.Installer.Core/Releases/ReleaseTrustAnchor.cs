using System.Reflection;

namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// The single release signing key this installer trusts.
///
/// The key is embedded rather than read from disk so that a release cannot be verified
/// against a key an attacker dropped next to the executable, and so that the installer
/// stays self-contained on a machine that has never seen this repository.
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
