namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// Where a release comes from.
///
/// There are two of these because there are two situations. A public repository is readable
/// by anyone over plain HTTPS, and requiring a GitHub account to install a published tool is
/// a barrier with nothing behind it. A private one is readable only by someone signed in, and
/// the GitHub CLI is how that sign-in is reused without the installer ever handling a token.
///
/// What does not change with the transport is what makes a release trustworthy: the manifest
/// is verified against the key embedded in this assembly, and the package against the hash in
/// that manifest. An anonymous download is therefore not a weaker download — nothing is
/// believed because of where it came from.
/// </summary>
public interface IReleaseFeed
{
    /// <summary>
    /// Turns a user-facing tag into a real one. "latest" is not a tag any repository has.
    /// </summary>
    Task<string> ResolveTagAsync(string requestedTag, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the manifest and its signature, and verifies them. A failure here means the
    /// release must not be installed.
    /// </summary>
    Task<ResolvedRelease> ResolveAsync(
        string tag,
        string workingDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the package archive and returns its path. The archive is unverified at this
    /// point; the caller hands it to <see cref="ReleasePackageVerifier"/>.
    /// </summary>
    Task<string> DownloadPackageAsync(
        string tag,
        string workingDirectory,
        IProgress<long>? bytesDownloaded,
        CancellationToken cancellationToken);
}
