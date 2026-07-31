using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Releases;

public sealed class ReleasePackageVerifier
{
    public const string PackageMetadataFileName = "localai-package.json";
    private const int MaximumEntryCount = 256;
    private const int MaximumMetadataSize = 64 * 1024;
    private const long MaximumEntrySize = 1024L * 1024 * 1024;
    private const long MaximumTotalUncompressedSize = 4L * 1024 * 1024 * 1024;
    private const int MaximumCompressionRatio = 100;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly HashSet<string> ReservedDosNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "COM¹", "COM²", "COM³",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "LPT¹", "LPT²", "LPT³",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly ReleaseManifestVerifier manifestVerifier;
    private readonly IReleaseClient releaseClient;
    private readonly IAuthenticodeVerifier authenticodeVerifier;
    private readonly AuthenticodePublisherPolicy publisherPolicy;
    private readonly IStagingRootFactory stagingRootFactory;

    public ReleasePackageVerifier(
        ReleaseManifestVerifier manifestVerifier,
        IReleaseClient releaseClient,
        IAuthenticodeVerifier authenticodeVerifier,
        AuthenticodePublisherPolicy publisherPolicy)
        : this(
            manifestVerifier,
            releaseClient,
            authenticodeVerifier,
            publisherPolicy,
            new WindowsStagingRootFactory())
    {
    }

    internal ReleasePackageVerifier(
        ReleaseManifestVerifier manifestVerifier,
        IReleaseClient releaseClient,
        IAuthenticodeVerifier authenticodeVerifier,
        AuthenticodePublisherPolicy publisherPolicy,
        IStagingRootFactory stagingRootFactory)
    {
        this.manifestVerifier = manifestVerifier ??
            throw new ArgumentNullException(nameof(manifestVerifier));
        this.releaseClient = releaseClient ??
            throw new ArgumentNullException(nameof(releaseClient));
        this.authenticodeVerifier = authenticodeVerifier ??
            throw new ArgumentNullException(nameof(authenticodeVerifier));
        this.publisherPolicy = publisherPolicy ??
            throw new ArgumentNullException(nameof(publisherPolicy));
        this.stagingRootFactory = stagingRootFactory ??
            throw new ArgumentNullException(nameof(stagingRootFactory));
    }

    public async Task<VerifiedPackage> VerifyAsync(
        ReadOnlyMemory<byte> manifestJson,
        ReadOnlyMemory<byte> signature,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        var manifest = manifestVerifier.Verify(manifestJson.Span, signature.Span);
        IStagingRootLease? stagingLease = null;
        try
        {
            stagingLease = stagingRootFactory.CreateExclusive(stagingRoot);
            var canonicalRoot = stagingLease.CanonicalPath;
            stagingLease.Revalidate();
            var archivePath = Path.Combine(canonicalRoot, ".package.zip");
            await DownloadAndHashAsync(
                manifest,
                archivePath,
                cancellationToken).ConfigureAwait(false);
            stagingLease.Revalidate();

            var entries = InspectArchive(archivePath);
            await ExtractAsync(
                archivePath,
                stagingLease,
                entries,
                cancellationToken).ConfigureAwait(false);
            File.Delete(archivePath);
            stagingLease.Revalidate();
            ValidateMetadata(manifest, canonicalRoot);
            ValidateFinalLayout(canonicalRoot);
            ValidateAuthenticode(manifest, canonicalRoot);
            stagingLease.Revalidate();
            var verified = new VerifiedPackage(manifest, stagingLease);
            stagingLease = null;
            return verified;
        }
        catch (OperationCanceledException)
        {
            CleanupOwned(stagingLease);
            throw;
        }
        catch (ReleaseVerificationException)
        {
            CleanupOwned(stagingLease);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException or
            CryptographicException or JsonException or DecoderFallbackException)
        {
            CleanupOwned(stagingLease);
            throw Failure();
        }
    }

    private async Task DownloadAndHashAsync(
        ReleaseManifest manifest,
        string archivePath,
        CancellationToken cancellationToken)
    {
        await using var source = await releaseClient.OpenPackageAsync(
            manifest.PackageUri,
            manifest.PackageSize,
            cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > manifest.PackageSize)
            {
                throw Failure();
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        var expectedHash = Convert.FromHexString(manifest.PackageSha256);
        var actualHash = hash.GetHashAndReset();
        if (total != manifest.PackageSize ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw Failure();
        }
    }

    private static IReadOnlyDictionary<string, CentralEntry> InspectArchive(
        string archivePath)
    {
        using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.RandomAccess);
        var eocd = FindEndOfCentralDirectory(stream);
        if (eocd.EntryCount > MaximumEntryCount)
        {
            throw Failure();
        }

        stream.Position = eocd.DirectoryOffset;
        var entries = new Dictionary<string, CentralEntry>(StringComparer.OrdinalIgnoreCase);
        var normalizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressed = 0;
        Span<byte> header = stackalloc byte[46];
        for (var index = 0; index < eocd.EntryCount; index++)
        {
            ReadExactly(stream, header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50)
            {
                throw Failure();
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);
            var externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(header[38..]);
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
            if ((flags & ~0x0800) != 0 ||
                method is not (0 or 8) ||
                disk != 0 ||
                compressed == uint.MaxValue ||
                uncompressed == uint.MaxValue ||
                localOffset >= eocd.DirectoryOffset ||
                nameLength == 0)
            {
                throw Failure();
            }

            var nameBytes = new byte[nameLength];
            ReadExactly(stream, nameBytes);
            var name = StrictUtf8.GetString(nameBytes);
            var extra = new byte[extraLength];
            ReadExactly(stream, extra);
            RejectUnsupportedExtraFields(extra);
            stream.Seek(commentLength, SeekOrigin.Current);
            ValidateArchiveName(name);
            var normalized = name.Normalize(NormalizationForm.FormC);
            if (!string.Equals(name, normalized, StringComparison.Ordinal) ||
                !normalizedNames.Add(normalized))
            {
                throw Failure();
            }

            var unixType = (externalAttributes >> 16) & 0xF000;
            const FileAttributes allowedWindowsAttributes =
                FileAttributes.ReadOnly |
                FileAttributes.Archive |
                FileAttributes.Normal;
            var windowsAttributes =
                (FileAttributes)(externalAttributes & 0xFFFF);
            if ((windowsAttributes & ~allowedWindowsAttributes) != 0 ||
                (windowsAttributes.HasFlag(FileAttributes.Normal) &&
                 windowsAttributes != FileAttributes.Normal) ||
                unixType is not (0 or 0x8000))
            {
                throw Failure();
            }

            if (uncompressed > MaximumEntrySize ||
                (string.Equals(name, PackageMetadataFileName, StringComparison.Ordinal) &&
                 uncompressed > MaximumMetadataSize) ||
                (uncompressed > 0 && compressed == 0) ||
                (compressed > 0 && uncompressed > compressed * (long)MaximumCompressionRatio))
            {
                throw Failure();
            }

            totalUncompressed = checked(totalUncompressed + uncompressed);
            if (totalUncompressed > MaximumTotalUncompressedSize ||
                !entries.TryAdd(name, new CentralEntry(
                    name,
                    nameBytes,
                    flags,
                    method,
                    BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
                    compressed,
                    uncompressed,
                    externalAttributes,
                    localOffset)))
            {
                throw Failure();
            }
        }

        if (stream.Position != eocd.DirectoryOffset + eocd.DirectorySize)
        {
            throw Failure();
        }

        ValidateLocalHeaders(stream, entries.Values, eocd.DirectoryOffset);

        var approved = new HashSet<string>(
            LocalAiPackageLayout.RequiredFiles.Append(PackageMetadataFileName),
            StringComparer.Ordinal);
        if (!approved.SetEquals(entries.Keys))
        {
            throw Failure();
        }

        return entries;
    }

    private static void ValidateLocalHeaders(
        FileStream stream,
        IEnumerable<CentralEntry> entries,
        long centralDirectoryOffset)
    {
        var offsets = new HashSet<long>();
        var ranges = new List<(long Start, long End)>();
        Span<byte> header = stackalloc byte[30];
        foreach (var entry in entries)
        {
            if (!offsets.Add(entry.LocalHeaderOffset))
            {
                throw Failure();
            }

            stream.Position = entry.LocalHeaderOffset;
            ReadExactly(stream, header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034B50)
            {
                throw Failure();
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[18..]);
            var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            if (flags != entry.Flags ||
                method != entry.CompressionMethod ||
                crc != entry.Crc32 ||
                compressed != entry.CompressedSize ||
                uncompressed != entry.UncompressedSize ||
                nameLength != entry.RawName.Length)
            {
                throw Failure();
            }

            var rawName = new byte[nameLength];
            ReadExactly(stream, rawName);
            if (!rawName.AsSpan().SequenceEqual(entry.RawName))
            {
                throw Failure();
            }

            var extra = new byte[extraLength];
            ReadExactly(stream, extra);
            RejectUnsupportedExtraFields(extra);
            var dataStart = stream.Position;
            var dataEnd = checked(dataStart + entry.CompressedSize);
            if (dataEnd > centralDirectoryOffset ||
                ranges.Any(range =>
                    entry.LocalHeaderOffset < range.End && dataEnd > range.Start))
            {
                throw Failure();
            }

            ranges.Add((entry.LocalHeaderOffset, dataEnd));
        }

        var ordered = ranges.OrderBy(range => range.Start).ToArray();
        if (ordered.Length == 0 || ordered[0].Start != 0 ||
            ordered[^1].End != centralDirectoryOffset)
        {
            throw Failure();
        }

        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].End != ordered[index].Start)
            {
                throw Failure();
            }
        }
    }

    private static async Task ExtractAsync(
        string archivePath,
        IStagingRootLease stagingLease,
        IReadOnlyDictionary<string, CentralEntry> inspected,
        CancellationToken cancellationToken)
    {
        var stagingRoot = stagingLease.CanonicalPath;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count != inspected.Count)
        {
            throw Failure();
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inspected.TryGetValue(entry.FullName, out var central) ||
                entry.Length != central.UncompressedSize ||
                entry.CompressedLength != central.CompressedSize ||
                entry.ExternalAttributes != unchecked((int)central.ExternalAttributes))
            {
                throw Failure();
            }

            stagingLease.Revalidate();
            var destinationPath = Path.GetFullPath(Path.Combine(stagingRoot, entry.FullName));
            EnsureContained(destinationPath, stagingRoot);
            await using var source = entry.Open();
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long written = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                written = checked(written + read);
                if (written > central.UncompressedSize)
                {
                    throw Failure();
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (written != central.UncompressedSize)
            {
                throw Failure();
            }

            stagingLease.ValidateCreatedFile(output.SafeFileHandle, destinationPath);
        }
    }

    private static void ValidateMetadata(
        ReleaseManifest manifest,
        string stagingRoot)
    {
        var path = Path.Combine(stagingRoot, PackageMetadataFileName);
        if (new FileInfo(path).Length > MaximumMetadataSize)
        {
            throw Failure();
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw Failure();
        }

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 3,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Failure();
        }

        var required = new HashSet<string>(
            [
                "SchemaVersion", "ReleaseVersion", "VersionDirectory",
                "ProtocolVersion", "BuildCompatibilityId",
            ],
            StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!required.Remove(property.Name))
            {
                throw Failure();
            }
        }

        if (required.Count != 0 ||
            root.GetProperty("SchemaVersion").ValueKind != JsonValueKind.Number ||
            root.GetProperty("SchemaVersion").GetInt32() != 1 ||
            root.GetProperty("ReleaseVersion").ValueKind != JsonValueKind.String ||
            !string.Equals(root.GetProperty("ReleaseVersion").GetString(), manifest.ReleaseVersion, StringComparison.Ordinal) ||
            root.GetProperty("VersionDirectory").ValueKind != JsonValueKind.String ||
            !string.Equals(root.GetProperty("VersionDirectory").GetString(), manifest.VersionDirectory, StringComparison.Ordinal) ||
            root.GetProperty("ProtocolVersion").ValueKind != JsonValueKind.Number ||
            root.GetProperty("ProtocolVersion").GetInt32() != manifest.ProtocolVersion ||
            root.GetProperty("ProtocolVersion").GetInt32() != BrokerCompatibilityContract.ProtocolVersion ||
            root.GetProperty("BuildCompatibilityId").ValueKind != JsonValueKind.String ||
            !string.Equals(root.GetProperty("BuildCompatibilityId").GetString(), manifest.BuildCompatibilityId, StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("BuildCompatibilityId").GetString(), BrokerCompatibilityContract.BuildCompatibilityId, StringComparison.Ordinal))
        {
            throw Failure();
        }

        var canonical = Encoding.UTF8.GetBytes(
            $"{{\"SchemaVersion\":1,\"ReleaseVersion\":{JsonSerializer.Serialize(manifest.ReleaseVersion)},\"VersionDirectory\":{JsonSerializer.Serialize(manifest.VersionDirectory)},\"ProtocolVersion\":{manifest.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"BuildCompatibilityId\":{JsonSerializer.Serialize(manifest.BuildCompatibilityId)}}}");
        if (!bytes.AsSpan().SequenceEqual(canonical))
        {
            throw Failure();
        }
    }

    private static void ValidateFinalLayout(string stagingRoot)
    {
        if (Directory.EnumerateDirectories(stagingRoot).Any())
        {
            throw Failure();
        }

        var expected = new HashSet<string>(
            LocalAiPackageLayout.RequiredFiles.Append(PackageMetadataFileName),
            StringComparer.Ordinal);
        var actual = Directory.EnumerateFiles(stagingRoot)
            .Select(path => Path.GetFileName(path)!)
            .ToArray();
        if (!expected.SetEquals(actual))
        {
            throw Failure();
        }
    }

    private void ValidateAuthenticode(
        ReleaseManifest manifest,
        string stagingRoot)
    {
        if (!manifest.RequiresAuthenticode)
        {
            return;
        }

        foreach (var file in LocalAiPackageLayout.RequiredFiles.Where(
                     file => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                             file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            if (!authenticodeVerifier.IsTrusted(
                    Path.Combine(stagingRoot, file),
                    publisherPolicy))
            {
                throw Failure();
            }
        }
    }

    private static void EnsureContained(string path, string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure();
        }
    }

    private static void ValidateArchiveName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name.Length > 240 ||
            name.Contains('\\') ||
            name.Contains('/') ||
            name.Contains(':') ||
            name[0] is '/' or '\\' ||
            name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(character => char.IsControl(character) || character == '\0'))
        {
            throw Failure();
        }

        var stem = name.Split('.')[0];
        if (name is "." or ".." || ReservedDosNames.Contains(stem))
        {
            throw Failure();
        }
    }

    private static EndOfCentralDirectory FindEndOfCentralDirectory(FileStream stream)
    {
        if (stream.Length < 22)
        {
            throw Failure();
        }

        var tailLength = (int)Math.Min(stream.Length, 65_557);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        ReadExactly(stream, tail);
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) != 0x06054B50)
            {
                continue;
            }

            var record = tail.AsSpan(index);
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
            var absoluteOffset = stream.Length - tailLength + index;
            if (disk != 0 || directoryDisk != 0 || diskEntries != entries ||
                entries == ushort.MaxValue || directorySize == uint.MaxValue ||
                directoryOffset == uint.MaxValue ||
                absoluteOffset + 22 + commentLength != stream.Length ||
                directoryOffset + (long)directorySize != absoluteOffset)
            {
                throw Failure();
            }

            return new EndOfCentralDirectory(entries, directoryOffset, directorySize);
        }

        throw Failure();
    }

    private static void RejectUnsupportedExtraFields(ReadOnlySpan<byte> extra)
    {
        while (!extra.IsEmpty)
        {
            if (extra.Length < 4)
            {
                throw Failure();
            }

            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (id == 0x0001 || extra.Length < 4 + length)
            {
                throw Failure();
            }

            extra = extra[(4 + length)..];
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                throw Failure();
            }

            total += read;
        }
    }

    private static void CleanupOwned(IStagingRootLease? lease)
    {
        if (lease is null)
        {
            return;
        }

        try
        {
            lease.Cleanup();
        }
        catch (Exception exception) when (
            IsExpectedCleanupFailure(exception))
        {
        }

        try
        {
            lease.Dispose();
        }
        catch (Exception exception) when (
            IsExpectedCleanupFailure(exception))
        {
        }
    }

    private static bool IsExpectedCleanupFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        ReleaseVerificationException or ObjectDisposedException or
        Win32Exception or System.Security.SecurityException or ArgumentException;

    private static ReleaseVerificationException Failure() =>
        new("Release package verification failed.");

    private sealed record CentralEntry(
        string Name,
        byte[] RawName,
        ushort Flags,
        ushort CompressionMethod,
        uint Crc32,
        long CompressedSize,
        long UncompressedSize,
        uint ExternalAttributes,
        long LocalHeaderOffset);

    private readonly record struct EndOfCentralDirectory(
        int EntryCount,
        long DirectoryOffset,
        long DirectorySize);
}
