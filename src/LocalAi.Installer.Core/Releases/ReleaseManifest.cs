using System.Collections.ObjectModel;

namespace LocalAi.Installer.Core.Releases;

public sealed record ManifestModel
{
    public ManifestModel(
        string name,
        int contextTokens,
        long downloadSize,
        long estimatedVramBytes)
    {
        Name = name;
        ContextTokens = contextTokens;
        DownloadSize = downloadSize;
        EstimatedVramBytes = estimatedVramBytes;
    }

    public string Name { get; }

    public int ContextTokens { get; }

    public long DownloadSize { get; }

    public long EstimatedVramBytes { get; }
}

public sealed record ReleaseManifest
{
    public ReleaseManifest(
        int schemaVersion,
        string releaseVersion,
        string versionDirectory,
        int protocolVersion,
        string buildCompatibilityId,
        Uri packageUri,
        long packageSize,
        string packageSha256,
        bool requiresAuthenticode,
        IReadOnlyList<ManifestModel> models)
    {
        SchemaVersion = schemaVersion;
        ReleaseVersion = releaseVersion;
        VersionDirectory = versionDirectory;
        ProtocolVersion = protocolVersion;
        BuildCompatibilityId = buildCompatibilityId;
        PackageUri = packageUri;
        PackageSize = packageSize;
        PackageSha256 = packageSha256;
        RequiresAuthenticode = requiresAuthenticode;
        Models = new ReadOnlyCollection<ManifestModel>(models.ToArray());
    }

    public int SchemaVersion { get; }

    public string ReleaseVersion { get; }

    public string VersionDirectory { get; }

    public int ProtocolVersion { get; }

    public string BuildCompatibilityId { get; }

    public Uri PackageUri { get; }

    public long PackageSize { get; }

    public string PackageSha256 { get; }

    public bool RequiresAuthenticode { get; }

    public IReadOnlyList<ManifestModel> Models { get; }
}

public sealed class VerifiedPackage : IDisposable
{
    private readonly object gate = new();
    private readonly IReadOnlyDictionary<string, IRetainedStagingFile> retainedByPath;
    private IReadOnlyList<IRetainedStagingFile>? retainedFiles;
    private IStagingRootLease? stagingLease;

    internal VerifiedPackage(
        ReleaseManifest manifest,
        IStagingRootLease stagingLease,
        IReadOnlyList<IRetainedStagingFile> retainedFiles)
    {
        Manifest = manifest;
        this.stagingLease = stagingLease;
        this.retainedFiles = retainedFiles.ToArray();
        retainedByPath = this.retainedFiles.ToDictionary(
            file => file.Metadata.RelativePath,
            StringComparer.Ordinal);
        Files = new ReadOnlyCollection<VerifiedPackageFile>(
            this.retainedFiles.Select(file => file.Metadata).ToArray());
        DiagnosticStagingRoot = stagingLease.CanonicalPath;
    }

    public ReleaseManifest Manifest { get; }

    /// <summary>
    /// The immutable allowlist Task 6 must use when installing this package.
    /// Task 6 must not enumerate or copy the staging tree by path.
    /// </summary>
    public IReadOnlyList<VerifiedPackageFile> Files { get; }

    internal string DiagnosticStagingRoot { get; }

    /// <summary>
    /// Opens an independent read-only stream backed by the retained verified
    /// file handle. Task 6 must consume content only through this method.
    /// </summary>
    public Stream OpenRead(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        lock (gate)
        {
            ThrowIfDisposed();
            if (!retainedByPath.TryGetValue(relativePath, out var file))
            {
                throw Failure();
            }

            stagingLease!.ValidateExactLayout(
                Files.Select(approved => approved.RelativePath));
            file.Revalidate();
            stagingLease.Revalidate();
            return file.OpenRead();
        }
    }

    public void Revalidate()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            RevalidateCore();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            var files = retainedFiles;
            var lease = stagingLease;
            retainedFiles = null;
            stagingLease = null;
            if (files is null)
            {
                return;
            }

            try
            {
                foreach (var file in files)
                {
                    file.Dispose();
                }
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    private void RevalidateCore()
    {
        var lease = stagingLease!;
        var files = retainedFiles!;
        lease.ValidateExactLayout(Files.Select(file => file.RelativePath));
        foreach (var file in files)
        {
            file.Revalidate();
        }

        lease.Revalidate();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(retainedFiles is null, this);

    private static ReleaseVerificationException Failure() =>
        new("Verified package content is unavailable.");
}

public sealed class VerifiedPackageFile
{
    internal VerifiedPackageFile(string relativePath, long length, string sha256)
    {
        RelativePath = relativePath;
        Length = length;
        Sha256 = sha256;
    }

    public string RelativePath { get; }

    public long Length { get; }

    public string Sha256 { get; }
}

public sealed class ReleaseVerificationException : Exception
{
    public ReleaseVerificationException(string message)
        : base(message)
    {
    }
}
