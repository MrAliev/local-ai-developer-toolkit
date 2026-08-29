using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The publisher policy compiled into the installer is a placeholder: CN=LocalAi and a SHA-256 of
/// sixty-four zeroes, because there is no code-signing certificate yet.
///
/// Enforcing it would not harden an installation — it would refuse every one, and refuse it with
/// a signature mismatch, which points at the package instead of at the flag that asked for the
/// check. A control nobody can satisfy is worse than an absent one, because it looks like a
/// control.
/// </summary>
public sealed class PlaceholderPublisherPolicyTests
{
    [Fact]
    public void An_all_zero_signer_hash_is_recognised_as_naming_nobody()
    {
        var policy = new AuthenticodePublisherPolicy("CN=LocalAi", new string('0', 64));

        Assert.True(policy.IsPlaceholder);
    }

    [Fact]
    public void A_real_signer_hash_is_not_a_placeholder()
    {
        var policy = new AuthenticodePublisherPolicy(
            "CN=Example Publisher, O=Example, C=US",
            new string('A', 64));

        Assert.False(policy.IsPlaceholder);
    }

    /// <summary>
    /// One zero short of the placeholder is a real hash, and has to be treated as one — the test
    /// exists because "looks mostly like zeroes" is exactly the kind of check that rots into a
    /// prefix comparison.
    /// </summary>
    [Fact]
    public void A_hash_that_merely_starts_with_zeroes_is_a_real_signer()
    {
        var policy = new AuthenticodePublisherPolicy(
            "CN=Example Publisher, O=Example, C=US",
            new string('0', 62) + "01");

        Assert.False(policy.IsPlaceholder);
    }
}
