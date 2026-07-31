using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class ReleasePackageVerifierTests : IDisposable
{
    private static readonly BigInteger P256Order = new(
        Convert.FromHexString(
            "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        isUnsigned: true,
        isBigEndian: true);
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(),
        "LocalAi-release-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VerifyAsync_accepts_signed_hash_bound_package()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var manifest = CreateManifest(package, requiresAuthenticode: true);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var signature = Sign(key, json);
        var auth = new RecordingAuthenticodeVerifier(true);
        var verifier = CreateVerifier(key, package, auth);

        using var verified = await verifier.VerifyAsync(
            json,
            signature,
            StagingPath(),
            TestContext.Current.CancellationToken);

        Assert.Equal(manifest.ReleaseVersion, verified.Manifest.ReleaseVersion);
        Assert.Equal(manifest.VersionDirectory, verified.Manifest.VersionDirectory);
        Assert.Equal(manifest.ModelCatalogVersion, verified.Manifest.ModelCatalogVersion);
        Assert.True(Path.IsPathFullyQualified(verified.DiagnosticStagingRoot));
        Assert.Equal(LocalAiPackageLayout.PackageArtifactFiles.Count, auth.Paths.Count);
        Assert.Equal(
            LocalAiPackageLayout.PackageArtifactFiles.Append(
                ReleasePackageVerifier.PackageMetadataFileName),
            verified.Files.Select(file => file.RelativePath));
        Assert.All(verified.Files, file =>
        {
            Assert.True(file.Length > 0);
            Assert.Matches("^[0-9A-F]{64}$", file.Sha256);
        });
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VerifiedPackageFile>)verified.Files).Clear());
        Assert.False(File.Exists(Path.Combine(
            verified.DiagnosticStagingRoot,
            ".package.zip")));
    }

    [Fact]
    public async Task Verified_package_locks_every_approved_file_until_disposed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var manifest = CreateManifest(package);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var verified = await CreateVerifier(key, package).VerifyAsync(
            json,
            Sign(key, json),
            StagingPath(),
            TestContext.Current.CancellationToken);
        var relative = LocalAiPackageLayout.RequiredFiles[0];
        var path = Path.Combine(verified.DiagnosticStagingRoot, relative);
        var replacement = Path.Combine(tempRoot, "replacement.bin");
        File.WriteAllText(replacement, "replacement");

        foreach (var file in verified.Files)
        {
            var approvedPath = Path.Combine(
                verified.DiagnosticStagingRoot,
                file.RelativePath);
            AssertBlocked(() => File.WriteAllText(approvedPath, "changed"));
            AssertBlocked(() => File.Delete(approvedPath));
        }

        AssertBlocked(() => File.Move(replacement, path, overwrite: true));

        verified.Dispose();
        foreach (var file in LocalAiPackageLayout.PackageArtifactFiles.Append(
                     ReleasePackageVerifier.PackageMetadataFileName))
        {
            var releasedPath = Path.Combine(verified.DiagnosticStagingRoot, file);
            File.WriteAllText(releasedPath, "released");
            Assert.Equal("released", File.ReadAllText(releasedPath));
        }

        Assert.Throws<ObjectDisposedException>(() => verified.OpenRead(relative));
    }

    [Fact]
    public async Task Authenticode_runs_while_each_path_is_locked_to_its_retained_identity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var manifest = CreateManifest(package, requiresAuthenticode: true);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var authenticode = new LockProbingAuthenticodeVerifier();

        using var verified = await CreateVerifier(key, package, authenticode).VerifyAsync(
            json,
            Sign(key, json),
            StagingPath(),
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalAiPackageLayout.PackageArtifactFiles.Count, authenticode.VerifiedCount);
    }

    [Fact]
    public async Task Manifest_bound_archive_stays_write_locked_through_extraction()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var manifest = CreateManifest(package);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var observer = new ArchiveLockProbe();
        var verifier = new ReleasePackageVerifier(
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo()),
            new MemoryReleaseClient(package),
            new RecordingAuthenticodeVerifier(true),
            new AuthenticodePublisherPolicy(
                "CN=Approved Publisher, O=LocalAi, C=US",
                new string('A', 64)),
            new WindowsStagingRootFactory(),
            observer);

        using var verified = await verifier.VerifyAsync(
            json,
            Sign(key, json),
            StagingPath(),
            TestContext.Current.CancellationToken);

        Assert.True(observer.WriteWasBlocked);
    }

    [Fact]
    public async Task Source_dispose_failure_releases_archive_and_cleans_owned_staging()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var manifest = CreateManifest(package);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var client = new DisposeFaultingReleaseClient(package);
        var staging = StagingPath();
        var verifier = new ReleasePackageVerifier(
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo()),
            client,
            new RecordingAuthenticodeVerifier(true),
            new AuthenticodePublisherPolicy(
                "CN=Approved Publisher, O=LocalAi, C=US",
                new string('A', 64)));

        var exception = await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            verifier.VerifyAsync(
                json,
                Sign(key, json),
                staging,
                TestContext.Current.CancellationToken));

        Assert.Equal("Release package verification failed.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.Stream.DisposeAsyncCount);
        Assert.False(Directory.Exists(staging));
        Directory.CreateDirectory(staging);
        Directory.Delete(staging);
    }

    [Theory]
    [InlineData("revalidate", "win32")]
    [InlineData("revalidate", "security")]
    [InlineData("revalidate", "unauthorized")]
    [InlineData("revalidate", "io")]
    [InlineData("retain", "win32")]
    [InlineData("retain", "security")]
    [InlineData("retain", "unauthorized")]
    [InlineData("retain", "io")]
    [InlineData("layout", "win32")]
    [InlineData("layout", "security")]
    [InlineData("layout", "unauthorized")]
    [InlineData("layout", "io")]
    public async Task VerifyAsync_sanitizes_native_identity_and_layout_failures(
        string stage,
        string failureKind)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var manifest = CreateManifest(package);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var staging = StagingPath();
        var verifier = new ReleasePackageVerifier(
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo()),
            new MemoryReleaseClient(package),
            new RecordingAuthenticodeVerifier(true),
            new AuthenticodePublisherPolicy(
                "CN=Approved Publisher, O=LocalAi, C=US",
                new string('A', 64)),
            new BoundaryFaultFactory(stage, CreateBoundaryFailure(failureKind)));

        var exception = await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            verifier.VerifyAsync(
                json,
                Sign(key, json),
                staging,
                TestContext.Current.CancellationToken));

        Assert.Equal("Release package verification failed.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(staging, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData("revalidate", "win32")]
    [InlineData("revalidate", "security")]
    [InlineData("revalidate", "unauthorized")]
    [InlineData("revalidate", "io")]
    [InlineData("layout", "win32")]
    [InlineData("layout", "security")]
    [InlineData("layout", "unauthorized")]
    [InlineData("layout", "io")]
    [InlineData("open", "win32")]
    [InlineData("open", "security")]
    [InlineData("open", "unauthorized")]
    [InlineData("open", "io")]
    [InlineData("read", "win32")]
    [InlineData("read", "security")]
    [InlineData("read", "unauthorized")]
    [InlineData("read", "io")]
    public void VerifiedPackage_sanitizes_native_content_boundary_failures(
        string stage,
        string failureKind)
    {
        var failure = CreateBoundaryFailure(failureKind);
        var lease = new BoundaryPackageLease(
            stage == "layout" ? failure : null);
        var retained = new BoundaryRetainedFile(stage, failure);
        using var package = new VerifiedPackage(
            CreateManifest([1, 2, 3]),
            lease,
            [retained]);

        var exception = stage switch
        {
            "revalidate" or "layout" =>
                Assert.Throws<ReleaseVerificationException>(package.Revalidate),
            "open" => Assert.Throws<ReleaseVerificationException>(() =>
                package.OpenRead(retained.Metadata.RelativePath)),
            _ => ReadFailure(package, retained.Metadata.RelativePath),
        };

        Assert.Equal("Verified package content is unavailable.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifiedPackage_preserves_cancellation_and_disposed_semantics()
    {
        var cancellation = new OperationCanceledException("caller cancellation");
        using var canceled = new VerifiedPackage(
            CreateManifest([1, 2, 3]),
            new BoundaryPackageLease(),
            [new BoundaryRetainedFile("revalidate", cancellation)]);

        Assert.Same(cancellation, Assert.ThrowsAny<OperationCanceledException>(
            canceled.Revalidate));

        var disposed = new VerifiedPackage(
            CreateManifest([1, 2, 3]),
            new BoundaryPackageLease(),
            [new BoundaryRetainedFile(null, null)]);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(disposed.Revalidate);
        Assert.Throws<ObjectDisposedException>(() => disposed.OpenRead("approved.bin"));
    }

    [Fact]
    public async Task Approved_content_is_read_from_retained_handle_and_excludes_added_files()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var manifest = CreateManifest(package);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        using var verified = await CreateVerifier(key, package).VerifyAsync(
            json,
            Sign(key, json),
            StagingPath(),
            TestContext.Current.CancellationToken);
        var relative = LocalAiPackageLayout.RequiredFiles[0];

        await using (var content = verified.OpenRead(relative))
        using (var reader = new StreamReader(content, Encoding.UTF8))
        {
            Assert.Equal("test " + relative, await reader.ReadToEndAsync(
                TestContext.Current.CancellationToken));
        }

        File.WriteAllText(
            Path.Combine(verified.DiagnosticStagingRoot, "added.dll"),
            "unapproved");
        Assert.DoesNotContain(
            verified.Files,
            file => string.Equals(file.RelativePath, "added.dll", StringComparison.Ordinal));
        Assert.Throws<ReleaseVerificationException>(() => verified.OpenRead("added.dll"));
        Assert.Throws<ReleaseVerificationException>(verified.Revalidate);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Retained_file_rejects_a_handle_bound_to_another_identity()
    {
        Directory.CreateDirectory(tempRoot);
        var expected = Path.Combine(tempRoot, "expected.bin");
        var other = Path.Combine(tempRoot, "other.bin");
        File.WriteAllText(expected, "expected");
        File.WriteAllText(other, "other");
        using SafeFileHandle wrongHandle = File.OpenHandle(
            other,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        Assert.Throws<ReleaseVerificationException>(() =>
            new WindowsRetainedStagingFile(
                "expected.bin",
                expected,
                wrongHandle));
    }

    [Theory]
    [MemberData(nameof(InvalidManifestJson))]
    public void Verify_rejects_noncanonical_or_unsafe_manifest(byte[] json)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new ReleaseManifestVerifier(
            key.ExportSubjectPublicKeyInfo());

        Assert.Throws<ReleaseVerificationException>(() =>
            verifier.Verify(json, Sign(key, json)));
    }

    public static TheoryData<byte[]> InvalidManifestJson()
    {
        var valid = Encoding.UTF8.GetString(
            ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(
                CreateManifest([1, 2, 3])));
        return new TheoryData<byte[]>
        {
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"SchemaVersion\":1,",
                "\"SchemaVersion\":1,\"Unknown\":1,",
                StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"SchemaVersion\":1,",
                "\"SchemaVersion\":1,\"SchemaVersion\":1,",
                StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("https://", "http://", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":\"1\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("\"Models\":[]", "\"Models\":null", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("\"Models\":[]", "\"Models\":[{\"Name\":\"safe:latest\",\"ContextTokens\":2048,\"DownloadSize\":1,\"EstimatedVramBytes\":1,\"Unknown\":true}]", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("\"Models\":[]", "\"Models\":[{\"Name\":\"safe:latest\",\"Name\":\"safe:latest\",\"ContextTokens\":2048,\"DownloadSize\":1,\"EstimatedVramBytes\":1}]", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("\"RequiresAuthenticode\":false,", string.Empty, StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid + "{}"),
            Encoding.UTF8.GetBytes(" " + valid),
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(valid)).ToArray(),
            new byte[] { 0x7B, 0x22, 0xC3, 0x28, 0x22, 0x3A, 0x31, 0x7D },
            Encoding.UTF8.GetBytes(valid.Replace("\"VersionDirectory\":\"1.2.3\"", "\"VersionDirectory\":\"..\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("\"ReleaseVersion\":\"1.2.3\"", "\"ReleaseVersion\":\"01.2.3\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("\"PackageSha256\":\"", "\"PackageSha256\":\"aa", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(valid.Replace("\"Models\":[]", "\"Models\":[{\"Name\":\"bad\\\\model\",\"ContextTokens\":3000,\"DownloadSize\":1,\"EstimatedVramBytes\":1}]", StringComparison.Ordinal)),
        };
    }

    [Fact]
    public void Manifest_snapshots_models()
    {
        var models = new List<ManifestModel>
        {
            new("safe:latest", 2048, 1, 1),
        };
        var original = CreateManifest([1, 2, 3]);
        var manifest = new ReleaseManifest(
            original.SchemaVersion, original.ReleaseVersion, original.VersionDirectory,
            original.ModelCatalogVersion,
            original.ProtocolVersion, original.BuildCompatibilityId, original.PackageUri,
            original.PackageSize, original.PackageSha256, false, models);

        models.Clear();

        Assert.Single(manifest.Models);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ManifestModel>)manifest.Models).Add(
                new ManifestModel("other:latest", 2048, 1, 1)));
    }

    [Fact]
    public void Verify_rejects_signature_tampering_and_wrong_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = CreateManifest([1, 2, 3]);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var signature = Sign(key, json);

        var wrongVerifier = new ReleaseManifestVerifier(
            wrongKey.ExportSubjectPublicKeyInfo());
        Assert.Throws<ReleaseVerificationException>(() =>
            wrongVerifier.Verify(json, signature));

        signature[0] ^= 0x80;
        var verifier = new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo());
        Assert.Throws<ReleaseVerificationException>(() =>
            verifier.Verify(json, signature));
        Assert.Throws<ReleaseVerificationException>(() =>
            verifier.Verify(json, new byte[65]));
    }

    [Fact]
    public void Verify_signature_binds_model_catalog_version()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = CreateManifest([1, 2, 3]);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var signature = Sign(key, json);
        var tampered = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(json).Replace(
                "\"ModelCatalogVersion\":\"signed-7\"",
                "\"ModelCatalogVersion\":\"signed-8\"",
                StringComparison.Ordinal));

        using var verifier = new ReleaseManifestVerifier(
            key.ExportSubjectPublicKeyInfo());
        Assert.Throws<ReleaseVerificationException>(() =>
            verifier.Verify(tampered, signature));
    }

    [Fact]
    public void Verify_rejects_high_s_twin_of_valid_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = CreateManifest([1, 2, 3]);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var lowSignature = Sign(key, json);
        var highSignature = lowSignature.ToArray();
        var lowS = new BigInteger(
            lowSignature.AsSpan(32),
            isUnsigned: true,
            isBigEndian: true);
        WriteScalar(P256Order - lowS, highSignature.AsSpan(32));

        Assert.True(key.VerifyData(
            json,
            highSignature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Throws<ReleaseVerificationException>(() =>
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo())
                .Verify(json, highSignature));
    }

    [Fact]
    public void Constructor_rejects_non_P256_public_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<ArgumentException>(() =>
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo()));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task VerifyAsync_rejects_package_hash_or_length_mismatch(
        bool changeHash,
        int sizeDelta)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var correct = CreateManifest(package);
        var manifest = new ReleaseManifest(
            correct.SchemaVersion,
            correct.ReleaseVersion,
            correct.VersionDirectory,
            correct.ModelCatalogVersion,
            correct.ProtocolVersion,
            correct.BuildCompatibilityId,
            correct.PackageUri,
            correct.PackageSize + sizeDelta,
            changeHash ? new string('A', 64) : correct.PackageSha256,
            correct.RequiresAuthenticode,
            correct.Models);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var staging = StagingPath();

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            CreateVerifier(key, package).VerifyAsync(
                json, Sign(key, json), staging,
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_rejects_missing_or_invalid_required_Authenticode(
        bool verifierSaysSigned)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();
        var manifest = CreateManifest(package, requiresAuthenticode: true);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        var auth = verifierSaysSigned
            ? new RecordingAuthenticodeVerifier(false, failAfter: 2)
            : new RecordingAuthenticodeVerifier(false);

        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            CreateVerifier(key, package, auth).VerifyAsync(
                json, Sign(key, json), StagingPath(),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("..\\escape.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("C:/drive.exe")]
    [InlineData("file.exe:stream")]
    [InlineData("CON")]
    [InlineData("CONIN$")]
    [InlineData("file. ")]
    public async Task VerifyAsync_rejects_unsafe_or_unexpected_archive_names(
        string entryName)
    {
        await AssertPackageRejected(CreatePackage(extraEntry: entryName));
    }

    [Theory]
    [InlineData(unchecked((int)0xA0000000))]
    [InlineData((int)FileAttributes.ReparsePoint)]
    public async Task VerifyAsync_rejects_link_reparse_and_special_entries(
        int externalAttributes)
    {
        await AssertPackageRejected(CreatePackage(
            extraEntry: "link",
            externalAttributes: externalAttributes));
    }

    [Theory]
    [InlineData((int)FileAttributes.Directory)]
    [InlineData((int)FileAttributes.Device)]
    [InlineData((int)FileAttributes.Encrypted)]
    [InlineData((int)FileAttributes.SparseFile)]
    [InlineData((int)FileAttributes.ReparsePoint)]
    [InlineData((int)FileAttributes.Offline)]
    [InlineData((int)FileAttributes.System)]
    [InlineData((int)FileAttributes.Hidden)]
    [InlineData((int)FileAttributes.Temporary)]
    public async Task VerifyAsync_rejects_windows_special_attributes_on_required_file(
        int attributes)
    {
        await AssertPackageRejected(CreatePackage(
            requiredFileExternalAttributes: attributes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData((int)FileAttributes.Archive)]
    [InlineData((int)FileAttributes.Normal)]
    [InlineData((int)FileAttributes.ReadOnly)]
    [InlineData((int)(FileAttributes.Archive | FileAttributes.ReadOnly))]
    public async Task VerifyAsync_accepts_only_regular_windows_file_attributes(
        int attributes)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage(requiredFileExternalAttributes: attributes);
        var manifest = CreateManifest(package);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);

        using var verified = await CreateVerifier(key, package).VerifyAsync(
            json,
            Sign(key, json),
            StagingPath(),
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(
            verified.DiagnosticStagingRoot,
            LocalAiPackageLayout.RequiredFiles[0])));
    }

    [Fact]
    public async Task VerifyAsync_rejects_duplicate_central_directory_names()
    {
        await AssertPackageRejected(CreatePackage(duplicateRequiredFile: true));
    }

    [Fact]
    public async Task VerifyAsync_rejects_encrypted_and_data_descriptor_flags()
    {
        foreach (var flag in new ushort[] { 0x0001, 0x0008 })
        {
            var package = MutateFirstEntry(CreatePackage(), (bytes, local, central, _) =>
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 6), flag);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 8), flag);
            });
            await AssertPackageRejected(package);
        }
    }

    [Fact]
    public async Task VerifyAsync_rejects_entry_count_and_aggregate_size_limits()
    {
        var count = MutateFirstEntry(CreatePackage(), (bytes, _, _, eocd) =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 8), 300);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 10), 300);
        });
        await AssertPackageRejected(count);

        var aggregate = CreatePackage();
        foreach (var central in FindAllSignatures(aggregate, 0x02014B50))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                aggregate.AsSpan(central + 20),
                7_000_000);
            BinaryPrimitives.WriteUInt32LittleEndian(
                aggregate.AsSpan(central + 24),
                700_000_000);
        }

        await AssertPackageRejected(aggregate);
    }

    [Fact]
    public async Task VerifyAsync_rejects_unsupported_method_and_zip64()
    {
        var method = MutateFirstEntry(CreatePackage(), (bytes, local, central, _) =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 8), 99);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 10), 99);
        });
        await AssertPackageRejected(method);

        var zip64 = MutateFirstEntry(CreatePackage(), (bytes, _, _, eocd) =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 8), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 10), ushort.MaxValue);
        });
        await AssertPackageRejected(zip64);
    }

    [Fact]
    public async Task VerifyAsync_rejects_per_file_size_limit()
    {
        var package = MutateFirstEntry(CreatePackage(), (bytes, _, central, _) =>
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(central + 24),
                1024U * 1024 * 1024 + 1));
        await AssertPackageRejected(package);
    }

    [Theory]
    [InlineData("flags")]
    [InlineData("method")]
    [InlineData("size")]
    [InlineData("name")]
    public async Task VerifyAsync_rejects_local_and_central_header_contradictions(
        string field)
    {
        var package = MutateFirstEntry(CreatePackage(), (bytes, local, _, _) =>
        {
            switch (field)
            {
                case "flags":
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 6), 0x0800);
                    break;
                case "method":
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 8), 8);
                    break;
                case "size":
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22), 99);
                    break;
                case "name":
                    bytes[local + 30] = bytes[local + 30] == (byte)'x' ? (byte)'y' : (byte)'x';
                    break;
            }
        });

        await AssertPackageRejected(package);
    }

    [Fact]
    public async Task VerifyAsync_rejects_zip_bomb_ratio()
    {
        await AssertPackageRejected(CreatePackage(
            extraEntry: "bomb.bin",
            extraContent: new byte[2 * 1024 * 1024]));
    }

    [Fact]
    public async Task VerifyAsync_rejects_missing_required_file()
    {
        await AssertPackageRejected(CreatePackage(
            missingFile: LocalAiPackageLayout.RequiredFiles[0]));
    }

    [Fact]
    public async Task VerifyAsync_rejects_missing_stable_launcher()
    {
        await AssertPackageRejected(CreatePackage(
            missingFile: LocalAiPackageLayout.StableLauncherFile));
    }

    [Theory]
    [InlineData("unexpected.txt")]
    [InlineData("nested/unexpected.txt")]
    [InlineData("LOCALAI.EXE")]
    public async Task VerifyAsync_rejects_unexpected_files_layout_and_case_duplicates(
        string entryName)
    {
        await AssertPackageRejected(CreatePackage(extraEntry: entryName));
    }

    [Fact]
    public async Task VerifyAsync_rejects_file_directory_conflicts_and_unicode_aliases()
    {
        await AssertPackageRejected(CreatePackage(
            extraEntries: ["node", "node/child"]));
        await AssertPackageRejected(CreatePackage(
            extraEntries: ["café.txt", "café.txt"]));
    }

    [Theory]
    [InlineData("ReleaseVersion", "9.9.9")]
    [InlineData("VersionDirectory", "9.9.9")]
    [InlineData("ModelCatalogVersion", "stale")]
    [InlineData("ProtocolVersion", "2")]
    [InlineData("BuildCompatibilityId", "LOCALAI-BROKER-V1")]
    public async Task VerifyAsync_rejects_internal_metadata_mismatch(
        string property,
        string value)
    {
        await AssertPackageRejected(CreatePackage(
            metadataOverride: (property, value)));
    }

    [Theory]
    [InlineData(2, "localai-broker-v1")]
    [InlineData(1, "LOCALAI-BROKER-V1")]
    public void Verify_rejects_incompatible_manifest_protocol_or_build(
        int protocol,
        string build)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = CreateManifest([1, 2, 3]);
        var manifest = new ReleaseManifest(
            1, original.ReleaseVersion, original.VersionDirectory,
            original.ModelCatalogVersion,
            protocol, build, original.PackageUri, original.PackageSize,
            original.PackageSha256, false, []);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);

        Assert.Throws<ReleaseVerificationException>(() =>
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo())
                .Verify(json, Sign(key, json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../catalog")]
    [InlineData("catalog/")]
    [InlineData(" catalog")]
    public void Verify_rejects_unsafe_model_catalog_version(string catalogVersion)
    {
        var original = CreateManifest([1, 2, 3]);
        var manifest = new ReleaseManifest(
            original.SchemaVersion,
            original.ReleaseVersion,
            original.VersionDirectory,
            catalogVersion,
            original.ProtocolVersion,
            original.BuildCompatibilityId,
            original.PackageUri,
            original.PackageSize,
            original.PackageSha256,
            original.RequiresAuthenticode,
            original.Models);

        Assert.Throws<ReleaseVerificationException>(() =>
            ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest));
    }

    [Fact]
    public void Verify_allows_same_model_name_at_distinct_supported_contexts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = CreateManifest([1, 2, 3]);
        var manifest = new ReleaseManifest(
            original.SchemaVersion, original.ReleaseVersion, original.VersionDirectory,
            original.ModelCatalogVersion,
            original.ProtocolVersion, original.BuildCompatibilityId, original.PackageUri,
            original.PackageSize, original.PackageSha256, original.RequiresAuthenticode,
            [
                new ManifestModel("model:latest", 2048, 7, 11),
                new ManifestModel("model:latest", 4096, 7, 11),
            ]);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);

        var verified = new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo())
            .Verify(json, Sign(key, json));

        Assert.Equal(2, verified.Models.Count);
    }

    [Fact]
    public void Verify_rejects_exact_duplicate_model_name_and_context()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = CreateManifest([1, 2, 3]);
        var manifest = new ReleaseManifest(
            original.SchemaVersion, original.ReleaseVersion, original.VersionDirectory,
            original.ModelCatalogVersion,
            original.ProtocolVersion, original.BuildCompatibilityId, original.PackageUri,
            original.PackageSize, original.PackageSha256, original.RequiresAuthenticode,
            [
                new ManifestModel("model:latest", 2048, 1, 1),
                new ManifestModel("model:latest", 2048, 1, 1),
            ]);
        Assert.Throws<ReleaseVerificationException>(() =>
            ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest));
    }

    [Fact]
    public void Verify_rejects_model_family_casing_mismatch_across_contexts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = CreateManifest([1, 2, 3]);
        var manifest = new ReleaseManifest(
            original.SchemaVersion, original.ReleaseVersion, original.VersionDirectory,
            original.ModelCatalogVersion,
            original.ProtocolVersion, original.BuildCompatibilityId, original.PackageUri,
            original.PackageSize, original.PackageSha256, original.RequiresAuthenticode,
            [
                new ManifestModel("model:latest", 2048, 7, 11),
                new ManifestModel("MODEL:latest", 4096, 7, 11),
            ]);

        Assert.Throws<ReleaseVerificationException>(() =>
            ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest));
    }

    [Fact]
    public void Verify_rejects_model_family_download_size_mismatch_across_contexts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = CreateManifest([1, 2, 3]);
        var manifest = new ReleaseManifest(
            original.SchemaVersion, original.ReleaseVersion, original.VersionDirectory,
            original.ModelCatalogVersion,
            original.ProtocolVersion, original.BuildCompatibilityId, original.PackageUri,
            original.PackageSize, original.PackageSha256, original.RequiresAuthenticode,
            [
                new ManifestModel("model:latest", 2048, 7, 11),
                new ManifestModel("model:latest", 4096, 8, 11),
            ]);

        Assert.Throws<ReleaseVerificationException>(() =>
            ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest));
    }

    [Fact]
    public void Verify_rejects_model_family_base_vram_mismatch_across_contexts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = CreateManifest([1, 2, 3]);
        var manifest = new ReleaseManifest(
            original.SchemaVersion, original.ReleaseVersion, original.VersionDirectory,
            original.ModelCatalogVersion,
            original.ProtocolVersion, original.BuildCompatibilityId, original.PackageUri,
            original.PackageSize, original.PackageSha256, original.RequiresAuthenticode,
            [
                new ManifestModel("model:latest", 2048, 7, 11),
                new ManifestModel("model:latest", 4096, 7, 12),
            ]);

        Assert.Throws<ReleaseVerificationException>(() =>
            ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest));
    }

    [Fact]
    public async Task VerifyAsync_requires_new_local_non_reparse_staging_root()
    {
        Directory.CreateDirectory(tempRoot);
        var existing = StagingPath();
        Directory.CreateDirectory(existing);

        await AssertPackageRejected(CreatePackage(), existing);
        Assert.True(Directory.Exists(existing));

        var nonLocal = @"\\server\share\stage-" + Guid.NewGuid().ToString("N");
        await AssertPackageRejected(CreatePackage(), nonLocal);
    }

    private async Task AssertPackageRejected(byte[] package, string? staging = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = CreateManifest(package);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        await Assert.ThrowsAsync<ReleaseVerificationException>(() =>
            CreateVerifier(key, package).VerifyAsync(
                json,
                Sign(key, json),
                staging ?? StagingPath(),
                TestContext.Current.CancellationToken));
    }

    private static void AssertBlocked(Action action)
    {
        var exception = Record.Exception(action);
        Assert.NotNull(exception);
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected a sharing violation, got {exception.GetType().Name}.");
    }

    private static ReleaseVerificationException ReadFailure(
        VerifiedPackage package,
        string relativePath)
    {
        using var stream = package.OpenRead(relativePath);
        return Assert.Throws<ReleaseVerificationException>(() => stream.ReadByte());
    }

    private static Exception CreateBoundaryFailure(string kind) => kind switch
    {
        "win32" => new Win32Exception(5, @"secret C:\private\identity"),
        "security" => new System.Security.SecurityException("secret security identity"),
        "unauthorized" => new UnauthorizedAccessException("secret unauthorized identity"),
        _ => new IOException("secret io identity"),
    };

    private static ReleasePackageVerifier CreateVerifier(
        ECDsa key,
        byte[] package,
        IAuthenticodeVerifier? authenticode = null) =>
        new(
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo()),
            new MemoryReleaseClient(package),
            authenticode ?? new RecordingAuthenticodeVerifier(true),
            new AuthenticodePublisherPolicy(
                "CN=Approved Publisher, O=LocalAi, C=US",
                new string('A', 64)));

    private static ReleaseManifest CreateManifest(
        byte[] package,
        bool requiresAuthenticode = false) =>
        new(
            1,
            "1.2.3",
            "1.2.3",
            "signed-7",
            BrokerCompatibilityContract.ProtocolVersion,
            BrokerCompatibilityContract.BuildCompatibilityId,
            new Uri("https://releases.example.invalid/localai-1.2.3.zip"),
            package.LongLength,
            Convert.ToHexString(SHA256.HashData(package)),
            requiresAuthenticode,
            []);

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
            WriteScalar(P256Order - s, signature.AsSpan(32));
        }

        return signature;
    }

    private static void WriteScalar(BigInteger value, Span<byte> destination)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        destination.Clear();
        bytes.CopyTo(destination[^bytes.Length..]);
    }

    private static byte[] CreatePackage(
        string? missingFile = null,
        string? extraEntry = null,
        byte[]? extraContent = null,
        int? externalAttributes = null,
        bool duplicateRequiredFile = false,
        (string Property, string Value)? metadataOverride = null,
        IReadOnlyList<string>? extraEntries = null,
        int? requiredFileExternalAttributes = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < LocalAiPackageLayout.PackageArtifactFiles.Count; index++)
            {
                var file = LocalAiPackageLayout.PackageArtifactFiles[index];
                if (string.Equals(file, missingFile, StringComparison.Ordinal))
                {
                    continue;
                }

                var entry = WriteEntry(
                    archive,
                    file,
                    Encoding.UTF8.GetBytes("test " + file));
                if (index == 0 && requiredFileExternalAttributes is { } attributes)
                {
                    entry.ExternalAttributes = attributes;
                }
            }

            if (duplicateRequiredFile)
            {
                WriteEntry(archive, LocalAiPackageLayout.RequiredFiles[0], [1]);
            }

            var metadata = new Dictionary<string, object>
            {
                ["SchemaVersion"] = 1,
                ["ReleaseVersion"] = "1.2.3",
                ["VersionDirectory"] = "1.2.3",
                ["ModelCatalogVersion"] = "signed-7",
                ["ProtocolVersion"] = BrokerCompatibilityContract.ProtocolVersion,
                ["BuildCompatibilityId"] = BrokerCompatibilityContract.BuildCompatibilityId,
            };
            if (metadataOverride is { } item)
            {
                metadata[item.Property] = item.Property == "ProtocolVersion"
                    ? int.Parse(item.Value, System.Globalization.CultureInfo.InvariantCulture)
                    : item.Value;
            }

            WriteEntry(
                archive,
                ReleasePackageVerifier.PackageMetadataFileName,
                Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(metadata)));

            if (extraEntry is not null)
            {
                var entry = archive.CreateEntry(extraEntry, CompressionLevel.SmallestSize);
                if (externalAttributes is { } attributes)
                {
                    entry.ExternalAttributes = attributes;
                }

                using var output = entry.Open();
                output.Write(extraContent ?? [1, 2, 3]);
            }

            foreach (var additional in extraEntries ?? [])
            {
                WriteEntry(archive, additional, [1]);
            }
        }

        return stream.ToArray();
    }

    private static ZipArchiveEntry WriteEntry(
        ZipArchive archive,
        string name,
        byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(content);
        return entry;
    }

    private static byte[] MutateFirstEntry(
        byte[] package,
        Action<byte[], int, int, int> mutation)
    {
        var local = FindSignature(package, 0x04034B50);
        var central = FindSignature(package, 0x02014B50);
        var eocd = FindSignature(package, 0x06054B50);
        mutation(package, local, central, eocd);
        return package;
    }

    private static int FindSignature(byte[] bytes, uint signature) =>
        FindAllSignatures(bytes, signature).First();

    private static IReadOnlyList<int> FindAllSignatures(byte[] bytes, uint signature)
    {
        var result = new List<int>();
        for (var index = 0; index <= bytes.Length - sizeof(uint); index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index)) == signature)
            {
                result.Add(index);
            }
        }

        return result;
    }

    private string StagingPath()
    {
        Directory.CreateDirectory(tempRoot);
        return Path.Combine(tempRoot, "stage-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class MemoryReleaseClient(byte[] content) : IReleaseClient
    {
        public Uri? RequestedUri { get; private set; }

        public Task<Stream> OpenPackageAsync(
            Uri approvedPackageUri,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedUri = approvedPackageUri;
            if (content.LongLength > maximumBytes)
            {
                throw new ReleaseVerificationException("Package exceeds the approved size.");
            }

            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }

    private sealed class DisposeFaultingReleaseClient(byte[] content) : IReleaseClient
    {
        public DisposeFaultingStream Stream { get; } = new(content);

        public Task<Stream> OpenPackageAsync(
            Uri approvedPackageUri,
            long maximumBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(Stream);
    }

    private sealed class DisposeFaultingStream(byte[] content)
        : MemoryStream(content, writable: false)
    {
        public int DisposeAsyncCount { get; private set; }

        public override ValueTask DisposeAsync()
        {
            DisposeAsyncCount++;
            return ValueTask.FromException(
                new IOException(@"secret C:\private\source-dispose"));
        }
    }

    private sealed class BoundaryFaultFactory(
        string stage,
        Exception failure) : IStagingRootFactory
    {
        public IStagingRootLease CreateExclusive(string requestedPath) =>
            new BoundaryFaultLease(
                new WindowsStagingRootFactory().CreateExclusive(requestedPath),
                stage,
                failure);
    }

    private sealed class BoundaryFaultLease(
        IStagingRootLease inner,
        string stage,
        Exception failure) : IStagingRootLease
    {
        public string CanonicalPath => inner.CanonicalPath;

        public void Revalidate()
        {
            if (stage == "revalidate")
            {
                throw failure;
            }

            inner.Revalidate();
        }

        public void ValidateCreatedFile(
            SafeFileHandle fileHandle,
            string expectedPath) =>
            inner.ValidateCreatedFile(fileHandle, expectedPath);

        public IRetainedStagingFile RetainFile(string relativePath) =>
            stage == "retain" ? throw failure : inner.RetainFile(relativePath);

        public void ValidateExactLayout(IEnumerable<string> approvedRelativePaths)
        {
            if (stage == "layout")
            {
                throw failure;
            }

            inner.ValidateExactLayout(approvedRelativePaths);
        }

        public void Cleanup() => inner.Cleanup();

        public void Dispose() => inner.Dispose();
    }

    private sealed class BoundaryPackageLease(Exception? layoutFailure = null)
        : IStagingRootLease
    {
        public string CanonicalPath => @"C:\diagnostic\staging";

        public void Revalidate()
        {
        }

        public void ValidateCreatedFile(
            SafeFileHandle fileHandle,
            string expectedPath)
        {
        }

        public IRetainedStagingFile RetainFile(string relativePath) =>
            throw new NotSupportedException();

        public void ValidateExactLayout(IEnumerable<string> approvedRelativePaths)
        {
            if (layoutFailure is not null)
            {
                throw layoutFailure;
            }
        }

        public void Cleanup()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class BoundaryRetainedFile(
        string? failureStage,
        Exception? failure) : IRetainedStagingFile
    {
        public VerifiedPackageFile Metadata { get; } =
            new("approved.bin", 3, new string('A', 64));

        public void Revalidate()
        {
            if (failureStage == "revalidate")
            {
                throw failure!;
            }
        }

        public Stream OpenRead()
        {
            if (failureStage == "open")
            {
                throw failure!;
            }

            return failureStage == "read"
                ? new BoundaryFaultingReadStream(failure!)
                : new MemoryStream([1, 2, 3], writable: false);
        }

        public byte[] ReadAllBytes(int maximumBytes) => [1, 2, 3];

        public void Dispose()
        {
        }
    }

    private sealed class BoundaryFaultingReadStream(Exception failure) : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override int Read(Span<byte> buffer) => throw failure;

        public override int ReadByte() => throw failure;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
    }

    private sealed class RecordingAuthenticodeVerifier(
        bool result,
        int? failAfter = null) : IAuthenticodeVerifier
    {
        public List<string> Paths { get; } = [];

        public bool IsTrusted(
            string path,
            AuthenticodePublisherPolicy publisherPolicy)
        {
            Paths.Add(path);
            return failAfter is { } count ? Paths.Count <= count : result;
        }
    }

    private sealed class LockProbingAuthenticodeVerifier : IAuthenticodeVerifier
    {
        public int VerifiedCount { get; private set; }

        public bool IsTrusted(
            string path,
            AuthenticodePublisherPolicy publisherPolicy)
        {
            AssertBlocked(() => File.WriteAllText(path, "tampered"));
            AssertBlocked(() => File.Delete(path));
            VerifiedCount++;
            return true;
        }
    }

    private sealed class ArchiveLockProbe : IReleaseVerificationObserver
    {
        public bool WriteWasBlocked { get; private set; }

        public void OnPackageHashed(string archivePath)
        {
            var exception = Record.Exception(() =>
            {
                using var ignored = new FileStream(
                    archivePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
            });
            WriteWasBlocked = exception is IOException or UnauthorizedAccessException;
        }
    }
}
