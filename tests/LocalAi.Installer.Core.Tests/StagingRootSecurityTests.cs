using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class StagingRootSecurityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi-staging-security-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Exclusive_factory_never_claims_or_deletes_an_existing_leaf()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "existing");
        Directory.CreateDirectory(staging);
        var sentinel = Path.Combine(staging, "caller.txt");
        File.WriteAllText(sentinel, "caller");
        var factory = new WindowsStagingRootFactory();

        Assert.Throws<ReleaseVerificationException>(() =>
            factory.CreateExclusive(staging));

        Assert.Equal("caller", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Post_create_pre_lease_failure_removes_only_marker_bound_owned_leaf()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "owned-failure");
        var factory = new WindowsStagingRootFactory(
            new ThrowAfterExclusiveCreate());

        Assert.Throws<ReleaseVerificationException>(() =>
            factory.CreateExclusive(staging));

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Post_create_replacement_is_never_deleted_as_owned()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "replacement");
        var factory = new WindowsStagingRootFactory(
            new ReplaceThenThrowAfterExclusiveCreate());

        Assert.Throws<ReleaseVerificationException>(() =>
            factory.CreateExclusive(staging));

        Assert.Equal(
            "foreign",
            File.ReadAllText(Path.Combine(staging, "foreign.txt")));
    }

    [Fact]
    public async Task Verifier_does_not_cleanup_when_concurrent_creator_wins_before_lease()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "race");
        var race = new ConcurrentWinnerFactory();
        var package = CreatePackage();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new ReleaseManifest(
            1, "1.2.3", "1.2.3",
            BrokerCompatibilityContract.ProtocolVersion,
            BrokerCompatibilityContract.BuildCompatibilityId,
            new Uri("https://example.invalid/package.zip"),
            package.Length,
            Convert.ToHexString(SHA256.HashData(package)),
            false,
            []);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var verifier = new ReleasePackageVerifier(
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo()),
            new MemoryClient(package),
            new AlwaysTrustedAuthenticode(),
            new AuthenticodePublisherPolicy("CN=Publisher, O=LocalAi, C=US", new string('A', 64)),
            race);

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            verifier.VerifyAsync(
                json,
                key.SignData(json, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                staging,
                TestContext.Current.CancellationToken));

        Assert.True(File.Exists(Path.Combine(staging, "winner.txt")));
        Assert.False(race.CleanupCalled);
    }

    [Fact]
    public async Task Deterministic_reparse_identity_failure_cleans_only_owned_lease()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "owned");
        var failingFactory = new IdentityFailureFactory();
        var package = CreatePackage();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new ReleaseManifest(
            1, "1.2.3", "1.2.3",
            BrokerCompatibilityContract.ProtocolVersion,
            BrokerCompatibilityContract.BuildCompatibilityId,
            new Uri("https://example.invalid/package.zip"),
            package.Length,
            Convert.ToHexString(SHA256.HashData(package)),
            false,
            []);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var verifier = new ReleasePackageVerifier(
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo()),
            new MemoryClient(package),
            new AlwaysTrustedAuthenticode(),
            new AuthenticodePublisherPolicy("CN=Publisher, O=LocalAi, C=US", new string('A', 64)),
            failingFactory);

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            verifier.VerifyAsync(
                json,
                key.SignData(json, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                staging,
                TestContext.Current.CancellationToken));

        Assert.True(failingFactory.Lease?.CleanupCalled);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Rejects_real_reparse_ancestor_when_links_are_supported()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows staging security is Windows-specific.");
            return;
        }

        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links requires Windows Developer Mode.");
            return;
        }

        Assert.Throws<ReleaseVerificationException>(() =>
            new WindowsStagingRootFactory().CreateExclusive(
                Path.Combine(link, "stage")));
        Assert.False(Directory.Exists(Path.Combine(outside, "stage")));
    }

    private static byte[] CreatePackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in LocalAiPackageLayout.RequiredFiles)
            {
                Write(archive, file, [1]);
            }

            Write(
                archive,
                ReleasePackageVerifier.PackageMetadataFileName,
                Encoding.UTF8.GetBytes(
                    "{\"SchemaVersion\":1,\"ReleaseVersion\":\"1.2.3\",\"VersionDirectory\":\"1.2.3\",\"ProtocolVersion\":1,\"BuildCompatibilityId\":\"localai-broker-v1\"}"));
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ConcurrentWinnerFactory : IStagingRootFactory
    {
        public bool CleanupCalled { get; private set; }

        public IStagingRootLease CreateExclusive(string requestedPath)
        {
            Directory.CreateDirectory(requestedPath);
            File.WriteAllText(Path.Combine(requestedPath, "winner.txt"), "winner");
            throw new ReleaseVerificationException("Staging root is unavailable.");
        }
    }

    private sealed class ThrowAfterExclusiveCreate : IStagingCreationObserver
    {
        public void AfterExclusiveCreate(string path) =>
            throw new ReleaseVerificationException("Injected pre-lease failure.");
    }

    private sealed class ReplaceThenThrowAfterExclusiveCreate : IStagingCreationObserver
    {
        public void AfterExclusiveCreate(string path)
        {
            Directory.Delete(path, recursive: true);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "foreign.txt"), "foreign");
            throw new ReleaseVerificationException("Injected replacement race.");
        }
    }

    private sealed class IdentityFailureFactory : IStagingRootFactory
    {
        public IdentityFailureLease? Lease { get; private set; }

        public IStagingRootLease CreateExclusive(string requestedPath)
        {
            Directory.CreateDirectory(requestedPath);
            Lease = new IdentityFailureLease(requestedPath);
            return Lease;
        }
    }

    private sealed class IdentityFailureLease(string path) : IStagingRootLease
    {
        public string CanonicalPath => path;

        public bool CleanupCalled { get; private set; }

        public void Revalidate() =>
            throw new ReleaseVerificationException("Simulated reparse identity change.");

        public void ValidateCreatedFile(
            Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
            string expectedPath) => Revalidate();

        public void Cleanup()
        {
            CleanupCalled = true;
            Directory.Delete(path, recursive: true);
        }

        public void Dispose()
        {
        }
    }

    private sealed class MemoryClient(byte[] bytes) : IReleaseClient
    {
        public Task<Stream> OpenPackageAsync(
            Uri approvedPackageUri,
            long maximumBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class AlwaysTrustedAuthenticode : IAuthenticodeVerifier
    {
        public bool IsTrusted(string path, AuthenticodePublisherPolicy policy) => true;
    }
}
