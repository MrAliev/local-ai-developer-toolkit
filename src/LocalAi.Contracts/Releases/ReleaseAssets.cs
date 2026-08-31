namespace LocalAi.Contracts;

/// <summary>
/// Where a published release lives and what its files are called.
///
/// Two things fetch these now: the installer, which downloads a release to install it, and the
/// runtime, which fetches only the manifest to see whether a newer one exists. They must agree
/// about the repository and the asset names or the second would be checking something other
/// than what the first would install — so the names are stated once, here, beside the manifest
/// format they belong to.
/// </summary>
public static class ReleaseAssets
{
    public const string DefaultRepository = "MrAliev/local-ai-developer-toolkit";

    public const string ManifestAsset = "release-manifest.json";

    public const string SignatureAsset = "release-manifest.sig";

    public const string PackageAsset = "localai-package.zip";

    /// <summary>
    /// The page that redirects to the newest release. Answering "which tag is newest" from
    /// this redirect costs no API quota, where the anonymous API allows sixty calls an hour
    /// per address and is shared with everyone behind the same one.
    /// </summary>
    public static Uri LatestRelease(string? repository = null) =>
        new($"https://github.com/{Resolve(repository)}/releases/latest");

    public static Uri LatestReleaseApi(string? repository = null) =>
        new($"https://api.github.com/repos/{Resolve(repository)}/releases/latest");

    /// <summary>The human page for one release: what a notice points a person at.</summary>
    public static Uri Release(string tag, string? repository = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return new(
            $"https://github.com/{Resolve(repository)}/releases/tag/" +
            Uri.EscapeDataString(tag));
    }

    public static Uri Asset(string tag, string assetName, string? repository = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        return new(
            $"https://github.com/{Resolve(repository)}/releases/download/" +
            $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}");
    }

    private static string Resolve(string? repository) =>
        string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository;
}
