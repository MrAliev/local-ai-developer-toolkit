using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Releases;

public sealed record ResolvedRelease(
    ReleaseManifest Manifest,
    byte[] ManifestJson,
    byte[] Signature);

public sealed class ReleaseResolutionException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Resolves a release from the project's GitHub releases.
///
/// The repository is private, so assets are fetched through the GitHub CLI rather than over
/// anonymous HTTP. That is deliberate: the installer never asks for, stores or even sees a
/// token — it reuses the sign-in the user already established on that machine with
/// <c>gh auth login</c>. A machine that is not signed in gets a clear message instead of an
/// unexplained download failure.
///
/// Whichever transport is used, the manifest is verified against the key embedded in the
/// installer before anything is downloaded on the strength of it.
/// </summary>
public sealed class GitHubReleaseFeed(
    IProcessRunner processRunner,
    string? repository = null,
    string? gitHubCliPath = null)
{
    public const string DefaultRepository = "MrAliev/local-ai-developer-toolkit";
    public const string ManifestAsset = "release-manifest.json";
    public const string SignatureAsset = "release-manifest.sig";
    public const string PackageAsset = "localai-package.zip";

    private static readonly TimeSpan DocumentTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(30);

    private readonly IProcessRunner processRunner =
        processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    private readonly string repository =
        string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository;

    private readonly string cliPath =
        string.IsNullOrWhiteSpace(gitHubCliPath) ? "gh" : gitHubCliPath;

    /// <summary>
    /// Downloads and verifies the manifest for a release tag. A failure here means the
    /// release must not be installed, so it is reported rather than worked around.
    /// </summary>
    public async Task<ResolvedRelease> ResolveAsync(
        string tag,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        Directory.CreateDirectory(workingDirectory);
        await DownloadAssetAsync(
                tag, ManifestAsset, workingDirectory, DocumentTimeout, cancellationToken)
            .ConfigureAwait(false);
        await DownloadAssetAsync(
                tag, SignatureAsset, workingDirectory, DocumentTimeout, cancellationToken)
            .ConfigureAwait(false);

        var manifestJson = await ReadAsync(workingDirectory, ManifestAsset, tag)
            .ConfigureAwait(false);
        var signature = await ReadAsync(workingDirectory, SignatureAsset, tag)
            .ConfigureAwait(false);

        try
        {
            using var verifier = ReleaseTrustAnchor.CreateManifestVerifier();
            return new ResolvedRelease(
                verifier.Verify(manifestJson, signature),
                manifestJson,
                signature);
        }
        catch (ReleaseVerificationException exception)
        {
            throw new ReleaseResolutionException(
                $"The manifest for release '{tag}' failed verification and will not be used.",
                exception);
        }
    }

    /// <summary>
    /// Downloads the package archive into <paramref name="workingDirectory"/> and returns
    /// its path. The archive is still unverified at this point; the caller hands it to
    /// <see cref="ReleasePackageVerifier"/>.
    /// </summary>
    public async Task<string> DownloadPackageAsync(
        string tag,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workingDirectory);
        await DownloadAssetAsync(
                tag, PackageAsset, workingDirectory, PackageTimeout, cancellationToken)
            .ConfigureAwait(false);
        return Path.Combine(workingDirectory, PackageAsset);
    }

    private async Task DownloadAssetAsync(
        string tag,
        string assetName,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                    cliPath,
                    [
                        "release", "download", tag,
                        "--repo", repository,
                        "--pattern", assetName,
                        "--dir", workingDirectory,
                        "--clobber",
                    ],
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ReleaseResolutionException(
                "The GitHub CLI could not be started. Install it and sign in with " +
                "'gh auth login' so the installer can read this private repository.",
                exception);
        }

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new ReleaseResolutionException(
                $"Could not download '{assetName}' from release '{tag}'. Check that this " +
                "computer is signed in with 'gh auth login' and that the release exists. " +
                detail.Trim());
        }
    }

    private static async Task<byte[]> ReadAsync(
        string workingDirectory,
        string assetName,
        string tag)
    {
        var path = Path.Combine(workingDirectory, assetName);
        if (!File.Exists(path))
        {
            throw new ReleaseResolutionException(
                $"Release '{tag}' does not publish '{assetName}'.");
        }

        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
    }
}
