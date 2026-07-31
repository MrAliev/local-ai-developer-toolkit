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

public sealed record VerifiedPackage(
    ReleaseManifest Manifest,
    string StagingRoot);

public sealed class ReleaseVerificationException : Exception
{
    public ReleaseVerificationException(string message)
        : base(message)
    {
    }
}
