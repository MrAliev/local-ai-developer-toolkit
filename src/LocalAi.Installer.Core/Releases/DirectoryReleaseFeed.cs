namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// Reads a release from a folder instead of from GitHub.
///
/// The installer is a downloader: the executable is a wizard, and the product is the package it
/// fetches while it runs. That makes "download it once and hand the folder to a colleague"
/// impossible, which is the wrong answer for an air-gapped machine, for a fork kept private, and
/// for anyone who would rather pass a folder than an invitation.
///
/// Trust is unchanged, because trust never came from the transport. The manifest is verified
/// against the key embedded in this assembly and the package against the SHA-256 inside that
/// manifest, exactly as over HTTPS. Reading bytes from a folder changes where they came from,
/// not whether they are believed — so a folder someone else prepared is no more trusted than a
/// host someone else runs.
///
/// The files are copied into the working directory rather than read where they lie. That is not
/// tidiness: verification and installation have to see the same bytes, and a source folder is
/// somewhere the installer does not control for the minutes an installation takes. Copying first
/// and verifying the copy closes the gap between the two.
///
/// Models still come from the Ollama registry. This makes an installation possible without
/// GitHub, not without the internet.
/// </summary>
public sealed class DirectoryReleaseFeed : IReleaseFeed
{
    /// <summary>
    /// The manifest verifier's own ceiling, so a file that is absurd rather than merely wrong is
    /// refused before it is copied anywhere.
    /// </summary>
    private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;

    private const long MaximumDocumentBytes = 64 * 1024;
    private const long MaximumSignatureBytes = 4 * 1024;
    private const int CopyBufferBytes = 1024 * 1024;

    private readonly string sourceDirectory;
    private readonly byte[] trustedPublicKey;

    /// <param name="trustedPublicKey">
    /// SubjectPublicKeyInfo of the key a manifest from this folder has to be signed with. Null
    /// means the one embedded in this assembly, which is the only value production uses; naming
    /// one is what lets a test sign a manifest of its own instead of needing the release key.
    /// </param>
    public DirectoryReleaseFeed(string sourceDirectory, byte[]? trustedPublicKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        this.sourceDirectory = Path.GetFullPath(sourceDirectory);
        this.trustedPublicKey = trustedPublicKey ?? ReleaseTrustAnchor.PublicKey;
    }

    /// <summary>
    /// The three files a folder has to hold, under the names the release publishes them with.
    /// </summary>
    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        GitHubReleaseFeed.ManifestAsset,
        GitHubReleaseFeed.SignatureAsset,
        GitHubReleaseFeed.PackageAsset,
    ];

    /// <summary>
    /// Whether a folder looks like a release, by name alone. Used to offer the folder the
    /// installer was started from without making the user type a path; it proves nothing about
    /// the contents, which is what verification is for.
    /// </summary>
    public static bool LooksLikeReleaseFolder(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        Directory.Exists(directory) &&
        RequiredFiles.All(name => File.Exists(Path.Combine(directory, name)));

    /// <summary>
    /// A folder holds one release, so "latest" is whichever one is in it. An explicit tag is
    /// answered only if it is that one — installing 0.1.44 because the folder happened to hold it
    /// when 0.1.45 was asked for is the kind of surprise an offline install can least afford.
    /// </summary>
    public async Task<string> ResolveTagAsync(
        string requestedTag,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadVerifiedManifestAsync(cancellationToken).ConfigureAwait(false);
        var available = manifest.ReleaseVersion;
        if (string.IsNullOrWhiteSpace(requestedTag) ||
            string.Equals(requestedTag.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
        {
            return available;
        }

        var requested = requestedTag.Trim();
        if (Matches(requested, available))
        {
            return available;
        }

        throw new ReleaseResolutionException(
            $"The folder '{sourceDirectory}' holds release {available}, not '{requested}'. " +
            "Point the installer at a folder holding the release you asked for, or ask for " +
            "the one that is there.");
    }

    public async Task<ResolvedRelease> ResolveAsync(
        string tag,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Directory.CreateDirectory(workingDirectory);

        var manifestJson = await CopyDocumentAsync(
                GitHubReleaseFeed.ManifestAsset,
                Path.Combine(workingDirectory, GitHubReleaseFeed.ManifestAsset),
                MaximumDocumentBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var signature = await CopyDocumentAsync(
                GitHubReleaseFeed.SignatureAsset,
                Path.Combine(workingDirectory, GitHubReleaseFeed.SignatureAsset),
                MaximumSignatureBytes,
                cancellationToken)
            .ConfigureAwait(false);

        return new ResolvedRelease(
            Verify(manifestJson, signature, tag),
            manifestJson,
            signature);
    }

    public async Task<string> DownloadPackageAsync(
        string tag,
        string workingDirectory,
        IProgress<long>? bytesDownloaded = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Directory.CreateDirectory(workingDirectory);

        var target = Path.Combine(workingDirectory, GitHubReleaseFeed.PackageAsset);
        await CopyFileAsync(
                GitHubReleaseFeed.PackageAsset,
                target,
                MaximumPackageBytes,
                bytesDownloaded,
                cancellationToken)
            .ConfigureAwait(false);
        return target;
    }

    private async Task<ReleaseManifest> ReadVerifiedManifestAsync(
        CancellationToken cancellationToken)
    {
        var manifestJson = await ReadAsync(
                GitHubReleaseFeed.ManifestAsset,
                MaximumDocumentBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var signature = await ReadAsync(
                GitHubReleaseFeed.SignatureAsset,
                MaximumSignatureBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return Verify(manifestJson, signature, tag: null);
    }

    /// <summary>
    /// Verification is what a folder is trusted through, so it happens before the version in the
    /// manifest is repeated back to anyone — including when it is only being used to name the
    /// release on a page.
    /// </summary>
    private ReleaseManifest Verify(byte[] manifestJson, byte[] signature, string? tag)
    {
        try
        {
            using var verifier = new ReleaseManifestVerifier(trustedPublicKey);
            return verifier.Verify(manifestJson, signature);
        }
        catch (ReleaseVerificationException exception)
        {
            throw new ReleaseResolutionException(
                tag is null
                    ? $"The manifest in '{sourceDirectory}' failed verification and will not " +
                      "be used."
                    : $"The manifest for release '{tag}' in '{sourceDirectory}' failed " +
                      "verification and will not be used.",
                exception);
        }
    }

    private async Task<byte[]> ReadAsync(
        string name,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var source = Open(name, maximumBytes);
        await using (source.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Copies one of the two small documents and hands back what was written, because the caller
    /// verifies exactly the bytes that landed rather than the ones that were read.
    /// </summary>
    private async Task<byte[]> CopyDocumentAsync(
        string name,
        string target,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await CopyFileAsync(name, target, maximumBytes, progress: null, cancellationToken)
            .ConfigureAwait(false);
        return await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyFileAsync(
        string name,
        string target,
        long maximumBytes,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var source = Open(name, maximumBytes);
        await using (source.ConfigureAwait(false))
        {
            var destination = new FileStream(
                target,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            await using (destination.ConfigureAwait(false))
            {
                var buffer = new byte[CopyBufferBytes];
                long total = 0;
                int read;
                while ((read = await source
                           .ReadAsync(buffer, cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    await destination
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
            }
        }
    }

    private FileStream Open(string name, long maximumBytes)
    {
        var path = Path.Combine(sourceDirectory, name);
        if (!File.Exists(path))
        {
            throw new ReleaseResolutionException(
                $"'{sourceDirectory}' does not hold {name}. An offline installation needs the " +
                "three files a release publishes: " + string.Join(", ", RequiredFiles) + ".");
        }

        var length = new FileInfo(path).Length;
        if (length > maximumBytes)
        {
            throw new ReleaseResolutionException(
                $"{name} in '{sourceDirectory}' is {length} bytes, which is larger than this " +
                $"installer will read ({maximumBytes} bytes).");
        }

        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ReleaseResolutionException(
                $"{name} in '{sourceDirectory}' could not be read: {exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// Releases are tagged with their version, and people write the tag with and without the
    /// leading v. Both name the same release, and refusing one of them would only teach the
    /// reader to distrust the message.
    /// </summary>
    private static bool Matches(string requested, string available) =>
        string.Equals(requested, available, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(requested.TrimStart('v', 'V'), available, StringComparison.OrdinalIgnoreCase);
}
