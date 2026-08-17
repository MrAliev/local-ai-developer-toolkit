using LocalAi.Installer.Core.Abstractions;
using System.Text.Json;

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

    private readonly IProcessFileRunner? fileProcessRunner =
        processRunner as IProcessFileRunner;

    private readonly string repository =
        string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository;

    private readonly string cliPath =
        string.IsNullOrWhiteSpace(gitHubCliPath) ? "gh" : gitHubCliPath;

    /// <summary>
    /// Turns a user-facing tag into a real one. "latest" is not a tag GitHub knows — asking
    /// for it by name returns 404 — so the newest published release is looked up instead.
    /// </summary>
    public async Task<string> ResolveTagAsync(
        string requestedTag,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(requestedTag) &&
            !string.Equals(requestedTag.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
        {
            return requestedTag.Trim();
        }

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                    cliPath,
                    [
                        "release", "view",
                        "--repo", repository,
                        "--json", "tagName",
                        "--jq", ".tagName",
                    ],
                    DocumentTimeout,
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

        var tag = result.StandardOutput?.Trim();
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(tag))
        {
            return tag;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var primaryDetail = ProcessDetail(result);
        ProcessResult fallback;
        try
        {
            fallback = await processRunner.RunAsync(
                    cliPath,
                    [
                        "release", "list",
                        "--repo", repository,
                        "--limit", "1",
                        "--exclude-drafts",
                        "--exclude-pre-releases",
                        "--json", "tagName",
                        "--jq", ".[0].tagName",
                    ],
                    DocumentTimeout,
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

        var fallbackTag = fallback.StandardOutput?.Trim();
        if (fallback.ExitCode == 0 && !string.IsNullOrWhiteSpace(fallbackTag))
        {
            return fallbackTag;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new ReleaseResolutionException(
            "Could not determine the newest release. Check that this computer is signed " +
            $"in with 'gh auth login'. Primary: {primaryDetail} " +
            $"Fallback: {ProcessDetail(fallback)}".Trim());
    }

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
        IProgress<long>? bytesDownloaded = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workingDirectory);
        var target = Path.Combine(workingDirectory, PackageAsset);

        // The CLI reports progress only on its own console, so bytes are observed from the
        // growing file instead. Verified against a real download: the file grows steadily
        // while the transfer runs.
        using var watcher = bytesDownloaded is null
            ? null
            : StartSizeWatcher(target, bytesDownloaded, cancellationToken);

        await DownloadAssetAsync(
                tag, PackageAsset, workingDirectory, PackageTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (File.Exists(target))
        {
            bytesDownloaded?.Report(new FileInfo(target).Length);
        }

        return target;
    }

    private static CancellationTokenSource StartSizeWatcher(
        string path,
        IProgress<long> progress,
        CancellationToken cancellationToken)
    {
        var watcher = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = watcher.Token;
        _ = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400), token)
                            .ConfigureAwait(false);
                        if (File.Exists(path))
                        {
                            progress.Report(new FileInfo(path).Length);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        // The file is being written; the next poll will see it.
                    }
                }
            },
            token);
        return watcher;
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

        if (result.ExitCode == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var primaryDetail = ProcessDetail(result);
        if (fileProcessRunner is null)
        {
            throw new ReleaseResolutionException(
                $"Could not download '{assetName}' from release '{tag}'. Check that this " +
                "computer is signed in with 'gh auth login' and that the release exists. " +
                primaryDetail);
        }

        await DownloadAssetByIdAsync(
                tag,
                assetName,
                workingDirectory,
                timeout,
                primaryDetail,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DownloadAssetByIdAsync(
        string tag,
        string assetName,
        string workingDirectory,
        TimeSpan timeout,
        string primaryDetail,
        CancellationToken cancellationToken)
    {
        var (owner, name) = RepositoryParts();
        ProcessResult lookup;
        try
        {
            lookup = await processRunner.RunAsync(
                    cliPath,
                    [
                        "api", "graphql",
                        "--method", "POST",
                        "--raw-field", $"query={ReleaseQuery}",
                        "--raw-field", $"owner={owner}",
                        "--raw-field", $"name={name}",
                        "--raw-field", $"tag={tag}",
                    ],
                    DocumentTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                "the GraphQL fallback could not be started",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (lookup.ExitCode != 0)
        {
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                $"GraphQL lookup: {ProcessDetail(lookup)}");
        }

        long releaseId;
        try
        {
            releaseId = ReadReleaseId(lookup.StandardOutput, tag);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                $"GraphQL lookup returned invalid release metadata: {exception.Message}",
                exception);
        }

        ProcessResult assetsLookup;
        try
        {
            assetsLookup = await processRunner.RunAsync(
                    cliPath,
                    [
                        "api",
                        $"repos/{owner}/{name}/releases/{releaseId}/assets?per_page=100",
                        "--method", "GET",
                    ],
                    DocumentTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                "the asset-list fallback could not be started",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (assetsLookup.ExitCode != 0)
        {
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                $"asset list: {ProcessDetail(assetsLookup)}");
        }

        ReleaseAsset asset;
        try
        {
            asset = ReadAsset(assetsLookup.StandardOutput, tag, assetName);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                $"asset list returned invalid release metadata: {exception.Message}",
                exception);
        }

        var target = Path.Combine(workingDirectory, assetName);
        DeleteIfExists(target);
        ProcessResult download;
        try
        {
            download = await fileProcessRunner!.RunToFileAsync(
                    cliPath,
                    [
                        "api",
                        $"repos/{owner}/{name}/releases/assets/{asset.DatabaseId}",
                        "--method", "GET",
                        "--header", "Accept: application/octet-stream",
                    ],
                    target,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DeleteIfExists(target);
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                "the asset-ID fallback could not be started",
                exception);
        }

        if (download.Cancelled && cancellationToken.IsCancellationRequested)
        {
            DeleteIfExists(target);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (download.ExitCode != 0)
        {
            DeleteIfExists(target);
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                $"asset-ID download: {ProcessDetail(download)}");
        }

        var actualSize = File.Exists(target) ? new FileInfo(target).Length : -1;
        if (actualSize != asset.Size)
        {
            DeleteIfExists(target);
            throw DownloadFailure(
                tag,
                assetName,
                primaryDetail,
                $"asset-ID download produced {actualSize} bytes; GitHub reports {asset.Size}");
        }
    }

    private (string Owner, string Name) RepositoryParts()
    {
        var parts = repository.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ReleaseResolutionException(
                $"GitHub repository '{repository}' must have the form 'owner/name'.");
        }

        return (parts[0], parts[1]);
    }

    private static long ReadReleaseId(string json, string tag)
    {
        using var document = JsonDocument.Parse(json);
        var repository = document.RootElement
            .GetProperty("data")
            .GetProperty("repository");
        if (!repository.TryGetProperty("release", out var release) ||
            release.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException($"Release '{tag}' was not found.");
        }

        var releaseId = release.GetProperty("databaseId").GetInt64();
        if (releaseId <= 0)
        {
            throw new InvalidOperationException(
                $"Release '{tag}' has invalid GitHub metadata.");
        }

        return releaseId;
    }

    private static ReleaseAsset ReadAsset(string json, string tag, string assetName)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var candidate in document.RootElement.EnumerateArray())
        {
            if (!string.Equals(
                    candidate.GetProperty("name").GetString(),
                    assetName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var databaseId = candidate.GetProperty("id").GetInt64();
            var size = candidate.GetProperty("size").GetInt64();
            if (databaseId <= 0 || size < 0)
            {
                throw new InvalidOperationException(
                    $"Asset '{assetName}' has invalid GitHub metadata.");
            }

            return new ReleaseAsset(databaseId, size);
        }

        throw new InvalidOperationException(
            $"Release '{tag}' does not publish '{assetName}'.");
    }

    private static string ProcessDetail(ProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            return detail.Trim();
        }

        if (result.TimedOut)
        {
            return "the command timed out";
        }

        if (result.Cancelled)
        {
            return "the command was cancelled";
        }

        return $"the command exited with code {result.ExitCode?.ToString() ?? "unknown"}";
    }

    private static ReleaseResolutionException DownloadFailure(
        string tag,
        string assetName,
        string primaryDetail,
        string fallbackDetail,
        Exception? inner = null) =>
        new(
            $"Could not download '{assetName}' from release '{tag}'. Check that this " +
            "computer is signed in with 'gh auth login' and that the release exists. " +
            $"Primary: {primaryDetail} Fallback: {fallbackDetail}",
            inner);

    private static void DeleteIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private const string ReleaseQuery =
        "query($owner:String!,$name:String!,$tag:String!){" +
        "repository(owner:$owner,name:$name){release(tagName:$tag){" +
        "databaseId}}}";

    private sealed record ReleaseAsset(long DatabaseId, long Size);

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
