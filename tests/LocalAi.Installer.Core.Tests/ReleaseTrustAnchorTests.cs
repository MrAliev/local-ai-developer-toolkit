using System.Security.Cryptography;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class ReleaseTrustAnchorTests
{
    [Fact]
    public void PublicKey_IsAnEcdsaP256SubjectPublicKeyInfo()
    {
        var key = ReleaseTrustAnchor.PublicKey;

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key, out var bytesRead);

        Assert.Equal(key.Length, bytesRead);
        Assert.Equal(
            ECCurve.NamedCurves.nistP256.Oid.Value,
            ecdsa.ExportParameters(false).Curve.Oid.Value);
    }

    [Fact]
    public void CreateManifestVerifier_AcceptsTheEmbeddedKey()
    {
        // The verifier constructor rejects anything that is not a P-256 SPKI key, so this
        // fails loudly at build time if the embedded resource is ever replaced by junk.
        using var verifier = ReleaseTrustAnchor.CreateManifestVerifier();

        Assert.NotNull(verifier);
    }

    [Fact]
    public void PublicKey_CannotBeMutatedThroughTheReturnedArray()
    {
        var first = ReleaseTrustAnchor.PublicKey;
        first[0] ^= 0xFF;

        Assert.NotEqual(first, ReleaseTrustAnchor.PublicKey);
    }
}
