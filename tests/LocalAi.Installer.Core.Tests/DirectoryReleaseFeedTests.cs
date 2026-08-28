using System.Numerics;
using System.Security.Cryptography;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Installing from a folder, with no network to GitHub.
///
/// The installer is a downloader: the executable is a wizard and the product is the package it
/// fetches while it runs, so handing someone the executable hands them nothing. What this feed
/// changes is where the bytes come from — and nothing about whether they are believed. The
/// manifest still has to be signed by the trusted key and the package still has to match the
/// hash inside that manifest, so a folder someone else prepared is no more trusted than a host
/// someone else runs.
/// </summary>
public sealed class DirectoryReleaseFeedTests : IDisposable
{
    private static readonly BigInteger P256Order = BigInteger.Parse(
        "115792089210356248762697446949407573529996955224135760342422259061068512044369");

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Directory.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public async Task Latest_is_whichever_release_the_folder_holds()
    {
        var source = Release("0.1.45");

        var tag = await Feed(source).ResolveTagAsync(
            "latest",
            TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
    }

    [Theory]
    [InlineData("0.1.45")]
    [InlineData("v0.1.45")]
    public async Task The_release_that_is_there_is_answered_by_either_spelling(string requested)
    {
        var source = Release("0.1.45");

        var tag = await Feed(source).ResolveTagAsync(
            requested,
            TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
    }

    /// <summary>
    /// Quietly installing whatever the folder happened to hold is the surprise an offline
    /// install can least afford: there is no release page to notice the wrong version on.
    /// </summary>
    [Fact]
    public async Task A_release_the_folder_does_not_hold_is_refused_rather_than_substituted()
    {
        var source = Release("0.1.44");

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("0.1.45", TestContext.Current.CancellationToken));

        Assert.Contains("0.1.44", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.1.45", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_manifest_and_signature_land_in_the_working_directory()
    {
        var source = Release("0.1.45");
        var working = Path.Combine(root, "working");

        var resolved = await Feed(source).ResolveAsync(
            "0.1.45",
            working,
            TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", resolved.Manifest.ReleaseVersion);
        Assert.True(File.Exists(Path.Combine(working, "release-manifest.json")));
        Assert.True(File.Exists(Path.Combine(working, "release-manifest.sig")));
    }

    [Fact]
    public async Task The_package_is_copied_and_its_progress_reported()
    {
        var source = Release("0.1.45");
        var working = Path.Combine(root, "working");
        var reported = new List<long>();

        var path = await Feed(source).DownloadPackageAsync(
            "0.1.45",
            working,
            new Progress<long>(reported.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(working, "localai-package.zip"), path);
        Assert.Equal(
            await File.ReadAllBytesAsync(
                Path.Combine(source, "localai-package.zip"),
                TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Copied rather than read where it lies: verification and installation have to see the same
    /// bytes, and the source folder is not somewhere the installer controls for the minutes an
    /// installation takes.
    /// </summary>
    [Fact]
    public async Task The_copy_is_what_survives_a_source_that_changes_afterwards()
    {
        var source = Release("0.1.45");
        var working = Path.Combine(root, "working");
        var path = await Feed(source).DownloadPackageAsync(
            "0.1.45",
            working,
            null,
            TestContext.Current.CancellationToken);

        await File.WriteAllBytesAsync(
            Path.Combine(source, "localai-package.zip"),
            [9, 9, 9],
            TestContext.Current.CancellationToken);

        Assert.NotEqual(
            new byte[] { 9, 9, 9 },
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_folder_missing_a_file_says_which_three_are_needed()
    {
        var source = Release("0.1.45");
        File.Delete(Path.Combine(source, "release-manifest.sig"));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Contains("release-manifest.sig", exception.Message, StringComparison.Ordinal);
        Assert.Contains("localai-package.zip", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point: a folder is not trusted for being a folder. A manifest signed by anything
    /// other than the embedded key is refused exactly as it would be over HTTPS.
    /// </summary>
    [Fact]
    public async Task A_manifest_signed_by_another_key_is_refused()
    {
        var source = Release("0.1.45");
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var feed = new DirectoryReleaseFeed(source, stranger.ExportSubjectPublicKeyInfo());

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            feed.ResolveAsync("0.1.45", Path.Combine(root, "working"), TestContext.Current.CancellationToken));

        Assert.Contains("failed verification", exception.Message, StringComparison.Ordinal);
        Assert.IsType<ReleaseVerificationException>(exception.InnerException);
    }

    [Fact]
    public void A_folder_is_recognised_only_when_all_three_files_are_there()
    {
        var source = Release("0.1.45");
        Assert.True(DirectoryReleaseFeed.LooksLikeReleaseFolder(source));

        File.Delete(Path.Combine(source, "localai-package.zip"));
        Assert.False(DirectoryReleaseFeed.LooksLikeReleaseFolder(source));
        Assert.False(DirectoryReleaseFeed.LooksLikeReleaseFolder(Path.Combine(root, "absent")));
    }

    private DirectoryReleaseFeed Feed(string source) =>
        new(source, key.ExportSubjectPublicKeyInfo());

    /// <summary>Writes the three files a release publishes, signed by this test's own key.</summary>
    private string Release(string version)
    {
        var source = Path.Combine(root, "release-" + version);
        Directory.CreateDirectory(source);
        var package = new byte[64 * 1024];
        Random.Shared.NextBytes(package);
        File.WriteAllBytes(Path.Combine(source, "localai-package.zip"), package);

        var manifest = new ReleaseManifest(
            1,
            version,
            version,
            "signed-7",
            BrokerCompatibilityContract.ProtocolVersion,
            BrokerCompatibilityContract.BuildCompatibilityId,
            new Uri("https://releases.example.invalid/localai-" + version + ".zip"),
            package.LongLength,
            Convert.ToHexString(SHA256.HashData(package)),
            false,
            []);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        File.WriteAllBytes(Path.Combine(source, "release-manifest.json"), json);
        File.WriteAllBytes(Path.Combine(source, "release-manifest.sig"), Sign(json));
        return source;
    }

    /// <summary>
    /// Low-S normalised, because the verifier rejects the other half of every signature pair —
    /// two encodings of one signature is a malleability the release format does not allow.
    /// </summary>
    private byte[] Sign(byte[] payload)
    {
        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var s = new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
        if (s > P256Order / 2)
        {
            var normalised = (P256Order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
            var destination = signature.AsSpan(32);
            destination.Clear();
            normalised.CopyTo(destination[^normalised.Length..]);
        }

        return signature;
    }

    public void Dispose()
    {
        key.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
