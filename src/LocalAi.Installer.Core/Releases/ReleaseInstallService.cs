using System.Runtime.Versioning;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// What the run did about models. <paramref name="Excluded"/> carries the models that were
/// never requested and why; <paramref name="Batch"/> is the broker's own answer for the ones
/// that were. Both are null-free and both are reported: a model silently absent from a
/// finished installation is the failure mode this record exists to prevent.
/// </summary>
public sealed record ReleaseModelInstallReport(
    IReadOnlyList<string> Excluded,
    BrokerModelInstallBatchResult? Batch)
{
    public static ReleaseModelInstallReport NotRequested { get; } = new([], null);
}

public sealed record ReleaseInstallResult(
    LocalAiPackageInstallStatus Status,
    string Version,
    string? PriorVersion,
    string VersionPath,
    string? Reason,
    ReleaseModelInstallReport? Models = null)
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
    IReleaseFeed feed,
    IProcessRunner processRunner,
    IFileSystemProbe fileSystemProbe)
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// For the commands around a model install -- status and preflight -- which answer promptly
    /// or not at all. It does not bound the pull: a download's duration belongs to the network,
    /// and BrokerModelInstaller gives it its own, far larger guard.
    /// </summary>
    private static readonly TimeSpan ModelCommandTimeout = TimeSpan.FromMinutes(30);

    private readonly IReleaseFeed feed =
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
        ModelProvisioningSelection? models = null,
        GpuSnapshot? gpu = null,
        IProgress<ModelProvisioningProgress>? modelProgress = null,
        Action<ReleaseInstallResult>? activated = null,
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

        // Ownership is explicit to the end of the flow (#204): the verifier cleans staging
        // up on a failed verification, but a successful install used to leave the whole
        // unpacked package behind because nothing here ever disposed what it was handed.
        try
        {
            var installer = new LocalAiPackageInstaller(
                processRunner,
                new ExistingLocalAiInspector(fileSystemProbe),
                ActivationTimeout);

            var result = await installer
                .InstallAsync(verified, InstallationLayout.CreateDefault(), cancellationToken)
                .ConfigureAwait(false);

            // Told to the caller here, between activation and the model pulls, so a run journal
            // can record the activation as done the moment it is. Journalling it only after the
            // models meant a process killed mid-pull left the activation - the one reversible
            // effect of this call - recorded as "state unknown".
            activated?.Invoke(new ReleaseInstallResult(
                result.Status,
                result.Version,
                result.PriorVersion,
                result.VersionPath,
                result.Reason));

            var modelReport = ReleaseModelInstallReport.NotRequested;
            var installed = result.Status is LocalAiPackageInstallStatus.Installed
                or LocalAiPackageInstallStatus.AlreadyInstalled;
            if (installed)
            {
                RecordInstalledRelease(
                    result.VersionPath,
                    result.Version,
                    release.Manifest.ReleaseVersion);
            }

            if (installed &&
                models is { Mode: not ModelProvisioningMode.None } &&
                OperatingSystem.IsWindows())
            {
                // Only after activation: the model work runs through the launcher this package
                // just published, and the broker installer checks it against the verified files.
                modelReport = await ProvisionModelsAsync(
                        verified,
                        models,
                        gpu ?? new GpuSnapshot(ObservationState.Unavailable, [], "No adapter information was collected."),
                        modelProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new ReleaseInstallResult(
                result.Status,
                result.Version,
                result.PriorVersion,
                result.VersionPath,
                result.Reason,
                modelReport);
        }
        finally
        {
            verified.TryCleanupAndDispose();
        }
    }

    /// <summary>
    /// Records which published release the activated directory came from.
    ///
    /// The pointer the launcher writes names a directory - a commit id - and nothing else on
    /// the machine knew that "467ed5f0f9bf" was published as 0.1.51, so every comparison
    /// against a newer release was a commit id against a version number and always concluded
    /// there was nothing to do (#255).
    ///
    /// A failure here never fails an installation that has already succeeded. What is lost is
    /// the ability to name the installed release until the next install writes it again, and
    /// the surfaces that read it say "unknown" rather than guessing.
    /// </summary>
    private static void RecordInstalledRelease(
        string versionPath,
        string versionDirectory,
        string releaseVersion)
    {
        try
        {
            // bin/versions/<directory> -> bin. Derived from the path that was just installed
            // rather than from InstallationLayout, which is Windows-only and would make this
            // whole method platform-bound for the sake of a value already in hand.
            var binRoot = Path.GetDirectoryName(Path.GetDirectoryName(versionPath));
            if (string.IsNullOrWhiteSpace(binRoot))
            {
                return;
            }

            new InstalledReleaseStore(binRoot).Write(versionDirectory, releaseVersion);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<ReleaseModelInstallReport> ProvisionModelsAsync(
        VerifiedPackage package,
        ModelProvisioningSelection selection,
        GpuSnapshot gpu,
        IProgress<ModelProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = ModelProvisioningPlanner.Create(
            package.Manifest.Models,
            gpu,
            selection);
        foreach (var excluded in plan.Excluded)
        {
            // A milestone: said once, about a decision, and the run log is where somebody
            // looks for it afterwards.
            progress?.Report(new ModelProvisioningProgress(
                excluded,
                0,
                plan.Requests.Count,
                IsMilestone: true));
        }

        if (plan.Requests.Count == 0)
        {
            return new(plan.Excluded, null);
        }

        progress?.Report(new ModelProvisioningProgress(
            "Models: " +
            string.Join(
                ", ",
                plan.Requests.Select(request =>
                    $"{request.Action.Model} at {request.Action.ContextSize} tokens")) +
            ". Anything already installed is left alone; the rest is downloaded now.",
            0,
            plan.Requests.Count,
            IsMilestone: true));

        using var lease = InstallationLayoutLease.Acquire(InstallationLayout.CreateDefault());
        using var modelInstaller = new BrokerModelInstaller(
            processRunner,
            lease,
            package,
            ModelCommandTimeout);
        var batch = await modelInstaller
            .InstallAsync(plan.Requests, progress, cancellationToken)
            .ConfigureAwait(false);
        return new(plan.Excluded, batch);
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
