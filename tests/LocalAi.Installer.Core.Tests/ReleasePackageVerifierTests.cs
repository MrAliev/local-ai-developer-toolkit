using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

public sealed class ReleasePackageVerifierTests : IDisposable
{
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

        var verified = await verifier.VerifyAsync(
            json,
            signature,
            StagingPath(),
            TestContext.Current.CancellationToken);

        Assert.Equal(manifest.ReleaseVersion, verified.Manifest.ReleaseVersion);
        Assert.Equal(manifest.VersionDirectory, verified.Manifest.VersionDirectory);
        Assert.True(Path.IsPathFullyQualified(verified.StagingRoot));
        Assert.Equal(LocalAiPackageLayout.RequiredFiles.Count, auth.Paths.Count);
        Assert.All(LocalAiPackageLayout.RequiredFiles, file =>
            Assert.True(File.Exists(Path.Combine(verified.StagingRoot, file))));
        Assert.False(File.Exists(Path.Combine(verified.StagingRoot, ".package.zip")));
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

    [Fact]
    public async Task VerifyAsync_rejects_duplicate_central_directory_names()
    {
        await AssertPackageRejected(CreatePackage(duplicateRequiredFile: true));
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

    [Theory]
    [InlineData("unexpected.txt")]
    [InlineData("nested/unexpected.txt")]
    [InlineData("LOCALAI.EXE")]
    public async Task VerifyAsync_rejects_unexpected_files_layout_and_case_duplicates(
        string entryName)
    {
        await AssertPackageRejected(CreatePackage(extraEntry: entryName));
    }

    [Theory]
    [InlineData("ReleaseVersion", "9.9.9")]
    [InlineData("VersionDirectory", "9.9.9")]
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
            protocol, build, original.PackageUri, original.PackageSize,
            original.PackageSha256, false, []);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);

        Assert.Throws<ReleaseVerificationException>(() =>
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo())
                .Verify(json, Sign(key, json)));
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

    private static ReleasePackageVerifier CreateVerifier(
        ECDsa key,
        byte[] package,
        IAuthenticodeVerifier? authenticode = null) =>
        new(
            new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo()),
            new MemoryReleaseClient(package),
            authenticode ?? new RecordingAuthenticodeVerifier(true),
            "Approved Publisher");

    private static ReleaseManifest CreateManifest(
        byte[] package,
        bool requiresAuthenticode = false) =>
        new(
            1,
            "1.2.3",
            "1.2.3",
            BrokerCompatibilityContract.ProtocolVersion,
            BrokerCompatibilityContract.BuildCompatibilityId,
            new Uri("https://releases.example.invalid/localai-1.2.3.zip"),
            package.LongLength,
            Convert.ToHexString(SHA256.HashData(package)),
            requiresAuthenticode,
            []);

    private static byte[] Sign(ECDsa key, byte[] payload) =>
        key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static byte[] CreatePackage(
        string? missingFile = null,
        string? extraEntry = null,
        byte[]? extraContent = null,
        int? externalAttributes = null,
        bool duplicateRequiredFile = false,
        (string Property, string Value)? metadataOverride = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in LocalAiPackageLayout.RequiredFiles)
            {
                if (string.Equals(file, missingFile, StringComparison.Ordinal))
                {
                    continue;
                }

                WriteEntry(archive, file, Encoding.UTF8.GetBytes("test " + file));
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
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(content);
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

    private sealed class RecordingAuthenticodeVerifier(
        bool result,
        int? failAfter = null) : IAuthenticodeVerifier
    {
        public List<string> Paths { get; } = [];

        public bool IsTrusted(string path, string approvedPublisher)
        {
            Paths.Add(path);
            return failAfter is { } count ? Paths.Count <= count : result;
        }
    }
}
