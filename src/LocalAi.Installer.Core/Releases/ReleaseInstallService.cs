using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Releases;

public sealed record ReleaseInstallResult(
    LocalAiPackageInstallStatus Status,
    string Version,
    string? PriorVersion,
    string VersionPath,
    string? Reason)
{
    public bool Installed =>
        Status is LocalAiPackageInstallStatus.Installed
            or LocalAiPackageInstallStatus.AlreadyInstalled;
}

/// <summary>
/// Downloads, verifies and activates a release.
///
/// Every step keeps the existing guarantees: the manifest is checked against the embedded
/// key before the package is fetched, the archive is checked against the manifest before
/// anything is extracted, and activation swaps the version pointer atomically.
/// </summary>
public sealed class ReleaseInstallService(
    GitHubReleaseFeed feed,
    IProcessRunner processRunner,
    IFileSystemProbe fileSystemProbe)
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMinutes(5);

    private readonly GitHubReleaseFeed feed =
        feed ?? throw new ArgumentNullException(nameof(feed));

    private readonly IProcessRunner processRunner =
        processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    private readonly IFileSystemProbe fileSystemProbe =
        fileSystemProbe ?? throw new ArgumentNullException(nameof(fileSystemProbe));

    public async Task<ReleaseInstallResult> InstallAsync(
        ResolvedRelease release,
        string workingDirectory,
        string tag,
        IProgress<long>? bytesDownloaded = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        // The release tag and the version inside the manifest are separate things: assets
        // live under the tag, so the tag is what the download needs.
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var packagePath = await feed
            .DownloadPackageAsync(tag, workingDirectory, bytesDownloaded, cancellationToken)
            .ConfigureAwait(false);

        var stagingRoot = Path.Combine(
            workingDirectory,
            "staging-" + Guid.NewGuid().ToString("N"));

        var verifier = new ReleasePackageVerifier(
            ReleaseTrustAnchor.CreateManifestVerifier(),
            new DownloadedFileReleaseClient(packagePath),
            new WindowsAuthenticodeVerifier(),
            // Consulted only when the manifest sets RequiresAuthenticode.
            new AuthenticodePublisherPolicy("CN=LocalAi", new string('0', 64)));

        var verified = await verifier
            .VerifyAsync(release.ManifestJson, release.Signature, stagingRoot, cancellationToken)
            .ConfigureAwait(false);

        var installer = new LocalAiPackageInstaller(
            processRunner,
            new ExistingLocalAiInspector(fileSystemProbe),
            ActivationTimeout);

        var result = await installer
            .InstallAsync(verified, InstallationLayout.CreateDefault(), cancellationToken)
            .ConfigureAwait(false);

        return new ReleaseInstallResult(
            result.Status,
            result.Version,
            result.PriorVersion,
            result.VersionPath,
            result.Reason);
    }

    /// <summary>
    /// Hands the verifier the archive that was already downloaded through the GitHub CLI.
    /// The package is fetched once, with the user's existing sign-in, rather than a second
    /// time over anonymous HTTP that a private repository would refuse.
    /// </summary>
    private sealed class DownloadedFileReleaseClient(string path) : IReleaseClient
    {
        public Task<Stream> OpenPackageAsync(
            Uri approvedPackageUri,
            long maximumBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(File.OpenRead(path));
    }
}
