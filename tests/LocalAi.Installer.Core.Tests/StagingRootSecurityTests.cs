using System.IO.Compression;
using System.Numerics;
using System.Runtime.Versioning;
using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class StagingRootSecurityTests : IDisposable
{
    private static readonly BigInteger P256Order = new(
        Convert.FromHexString(
            "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        isUnsigned: true,
        isBigEndian: true);
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

    [Theory]
    [InlineData((int)StagingCreationStage.LeaseConstruction)]
    [InlineData((int)StagingCreationStage.NonceGeneration)]
    [InlineData((int)StagingCreationStage.MarkerOpen)]
    [InlineData((int)StagingCreationStage.PartialMarkerWrite)]
    [InlineData((int)StagingCreationStage.MarkerFlush)]
    [InlineData((int)StagingCreationStage.PostMarker)]
    public void Every_post_create_failure_removes_safe_known_owned_leaf(
        int stageValue)
    {
        var stage = (StagingCreationStage)stageValue;
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "owned-failure-" + stage);
        var factory = new WindowsStagingRootFactory(
            new NativeNtAtomicDirectoryCreator(),
            new ThrowAtCreationStage(stage));

        Assert.Throws<ReleaseVerificationException>(() =>
            factory.CreateExclusive(staging));

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Post_create_replacement_is_never_deleted_as_owned()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "replacement");
        var creator = new MisboundAtomicDirectoryCreator("foreign");
        var factory = new WindowsStagingRootFactory(creator);

        Assert.Throws<ReleaseVerificationException>(() =>
            factory.CreateExclusive(staging));

        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(Path.Combine(root, "foreign")));
    }

    [Fact]
    public void Atomic_native_create_failure_leaves_nothing_and_is_not_retried()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "native-failure");
        var creator = new FailingAtomicDirectoryCreator();
        var factory = new WindowsStagingRootFactory(creator);

        Assert.Throws<ReleaseVerificationException>(() =>
            factory.CreateExclusive(staging));

        Assert.Equal(1, creator.CallCount);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Native_staging_failure_is_sanitized()
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "native-secret");
        var factory = new WindowsStagingRootFactory(
            new ThrowingAtomicDirectoryCreator(
                new Win32Exception(5, @"secret C:\private\stage")));

        var exception = Assert.Throws<ReleaseVerificationException>(() =>
            factory.CreateExclusive(staging));

        Assert.Equal("Secure staging root creation failed.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(staging, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Native_atomic_create_returns_bound_handle_and_collision_is_never_owned()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Native atomic staging creation is Windows-specific.");
            return;
        }

        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "native-owned");
        using var lease = new WindowsStagingRootFactory().CreateExclusive(staging);
        lease.Revalidate();
        File.WriteAllText(Path.Combine(staging, "sentinel.txt"), "owned");

        Assert.Throws<ReleaseVerificationException>(() =>
            new WindowsStagingRootFactory().CreateExclusive(staging));

        Assert.Equal("owned", File.ReadAllText(Path.Combine(staging, "sentinel.txt")));
    }

    [Theory]
    [SupportedOSPlatform("windows")]
    [InlineData((int)InheritanceFlags.None, (int)PropagationFlags.None)]
    [InlineData(
        (int)(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit),
        (int)PropagationFlags.NoPropagateInherit)]
    public void Revalidate_rejects_full_control_ace_with_changed_inheritance_semantics(
        int inheritanceValue,
        int propagationValue)
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "acl-semantics");
        using var lease = new WindowsStagingRootFactory().CreateExclusive(staging);
        var directory = new DirectoryInfo(staging);
        var security = directory.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        security.RemoveAccessRuleAll(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            (InheritanceFlags)inheritanceValue,
            (PropagationFlags)propagationValue,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        Assert.Throws<ReleaseVerificationException>(lease.Revalidate);
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
                Sign(key, json),
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
                Sign(key, json),
                staging,
                TestContext.Current.CancellationToken));

        Assert.True(failingFactory.Lease?.CleanupCalled);
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData("win32", false)]
    [InlineData("security", false)]
    [InlineData("argument", false)]
    [InlineData("win32", true)]
    [InlineData("security", true)]
    [InlineData("argument", true)]
    public async Task Cleanup_failure_never_masks_verification_failure_or_cancellation(
        string cleanupFailure,
        bool cancel)
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "cleanup-primary-" + Guid.NewGuid().ToString("N"));
        Exception primary = cancel
            ? new OperationCanceledException("primary cancellation")
            : new ReleaseVerificationException("primary verification failure");
        Exception cleanup = cleanupFailure switch
        {
            "win32" => new Win32Exception(5, "secret native cleanup failure"),
            "security" => new System.Security.SecurityException("secret security cleanup failure"),
            _ => new ArgumentException("secret argument cleanup failure"),
        };
        var factory = new CleanupFailureFactory(staging, primary, cleanup);
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
            factory);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
            verifier.VerifyAsync(
                json,
                Sign(key, json),
                staging,
                TestContext.Current.CancellationToken));

        Assert.Same(primary, thrown);
        Assert.True(factory.Lease.CleanupCalled);
        Assert.True(factory.Lease.Disposed);
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

        var creator = new FailingAtomicDirectoryCreator();
        Assert.Throws<ReleaseVerificationException>(() =>
            new WindowsStagingRootFactory(creator).CreateExclusive(
                Path.Combine(link, "stage")));
        Assert.Equal(0, creator.CallCount);
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

    private static byte[] Sign(ECDsa key, byte[] payload)
    {
        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var s = new BigInteger(
            signature.AsSpan(32),
            isUnsigned: true,
            isBigEndian: true);
        if (s > P256Order / 2)
        {
            var canonical = (P256Order - s).ToByteArray(
                isUnsigned: true,
                isBigEndian: true);
            signature.AsSpan(32).Clear();
            canonical.CopyTo(signature.AsSpan(64 - canonical.Length));
        }

        return signature;
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

    private sealed class ThrowAtCreationStage(
        StagingCreationStage failureStage) : IStagingCreationObserver
    {
        public void OnStage(StagingCreationStage stage, string path)
        {
            if (stage == failureStage)
            {
                throw new ReleaseVerificationException(
                    "Injected pre-lease failure at " + stage + ".");
            }
        }
    }

    private sealed class FailingAtomicDirectoryCreator : IAtomicDirectoryCreator
    {
        public int CallCount { get; private set; }

        public Microsoft.Win32.SafeHandles.SafeFileHandle CreateDirectory(
            Microsoft.Win32.SafeHandles.SafeFileHandle parentHandle,
            string leafName,
            ReadOnlySpan<byte> securityDescriptor)
        {
            CallCount++;
            throw new ReleaseVerificationException("Injected native create failure.");
        }
    }

    private sealed class MisboundAtomicDirectoryCreator(string foreignLeaf)
        : IAtomicDirectoryCreator
    {
        public Microsoft.Win32.SafeHandles.SafeFileHandle CreateDirectory(
            Microsoft.Win32.SafeHandles.SafeFileHandle parentHandle,
            string leafName,
            ReadOnlySpan<byte> securityDescriptor) =>
            new NativeNtAtomicDirectoryCreator().CreateDirectory(
                parentHandle,
                foreignLeaf,
                securityDescriptor);
    }

    private sealed class ThrowingAtomicDirectoryCreator(Exception failure)
        : IAtomicDirectoryCreator
    {
        public Microsoft.Win32.SafeHandles.SafeFileHandle CreateDirectory(
            Microsoft.Win32.SafeHandles.SafeFileHandle parentHandle,
            string leafName,
            ReadOnlySpan<byte> securityDescriptor) => throw failure;
    }

    private sealed class CleanupFailureFactory(
        string path,
        Exception primary,
        Exception cleanupFailure) : IStagingRootFactory
    {
        public CleanupFailureLease Lease { get; } =
            new(path, primary, cleanupFailure);

        public IStagingRootLease CreateExclusive(string requestedPath)
        {
            Directory.CreateDirectory(path);
            return Lease;
        }
    }

    private sealed class CleanupFailureLease(
        string path,
        Exception primary,
        Exception cleanupFailure) : IStagingRootLease
    {
        public string CanonicalPath => path;

        public bool CleanupCalled { get; private set; }

        public bool Disposed { get; private set; }

        public void Revalidate() => throw primary;

        public void ValidateCreatedFile(
            Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
            string expectedPath) => throw primary;

        public void Cleanup()
        {
            CleanupCalled = true;
            throw cleanupFailure;
        }

        public void Dispose() => Disposed = true;
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
