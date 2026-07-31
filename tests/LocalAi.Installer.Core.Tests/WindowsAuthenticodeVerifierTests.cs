using System.Security.Cryptography;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class WindowsAuthenticodeVerifierTests
{
    private const string DistinguishedName = "CN=LocalAi Release, O=LocalAi, C=US";
    private static readonly string SpkiHash = new('A', 64);

    [Fact]
    public void Accepts_only_state_bound_signers_matching_full_dn_and_spki()
    {
        var provider = new FakeSignerProvider(
            new AuthenticodeSignerIdentity(DistinguishedName, Convert.FromHexString(SpkiHash)));
        var verifier = new WindowsAuthenticodeVerifier(provider);
        var policy = new AuthenticodePublisherPolicy(DistinguishedName, SpkiHash);

        Assert.True(verifier.IsTrusted("signed.exe", policy));
        Assert.Equal(Path.GetFullPath("signed.exe"), provider.Path);
    }

    [Theory]
    [InlineData("CN=LocalAi Release", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("CN=LocalAi Release, O=LocalAi, C=US", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public void Rejects_any_signer_identity_mismatch(string dn, string hash)
    {
        var provider = new FakeSignerProvider(
            new AuthenticodeSignerIdentity(dn, Convert.FromHexString(hash)));
        var verifier = new WindowsAuthenticodeVerifier(provider);

        Assert.False(verifier.IsTrusted(
            "signed.exe",
            new AuthenticodePublisherPolicy(DistinguishedName, SpkiHash)));
    }

    [Fact]
    public void Rejects_mixed_multiple_signers_deterministically()
    {
        var provider = new FakeSignerProvider(
            new AuthenticodeSignerIdentity(DistinguishedName, Convert.FromHexString(SpkiHash)),
            new AuthenticodeSignerIdentity(DistinguishedName, SHA256.HashData([1])));
        var verifier = new WindowsAuthenticodeVerifier(provider);

        Assert.False(verifier.IsTrusted(
            "signed.exe",
            new AuthenticodePublisherPolicy(DistinguishedName, SpkiHash)));
    }

    [Fact]
    public void Rejects_no_signer_or_native_verification_failure()
    {
        Assert.False(new WindowsAuthenticodeVerifier(new FakeSignerProvider())
            .IsTrusted("unsigned.exe", new AuthenticodePublisherPolicy(DistinguishedName, SpkiHash)));
        Assert.False(new WindowsAuthenticodeVerifier(new FakeSignerProvider(throwFailure: true))
            .IsTrusted("bad.exe", new AuthenticodePublisherPolicy(DistinguishedName, SpkiHash)));
    }

    private sealed class FakeSignerProvider : IWinTrustSignerProvider
    {
        private readonly IReadOnlyList<AuthenticodeSignerIdentity> signers;
        private readonly bool throwFailure;

        public FakeSignerProvider(params AuthenticodeSignerIdentity[] signers)
            : this(false, signers)
        {
        }

        public FakeSignerProvider(bool throwFailure, params AuthenticodeSignerIdentity[] signers)
        {
            this.throwFailure = throwFailure;
            this.signers = signers;
        }

        public string? Path { get; private set; }

        public IReadOnlyList<AuthenticodeSignerIdentity> GetVerifiedSigners(string path)
        {
            Path = path;
            if (throwFailure)
            {
                throw new CryptographicException();
            }

            return signers;
        }
    }
}
