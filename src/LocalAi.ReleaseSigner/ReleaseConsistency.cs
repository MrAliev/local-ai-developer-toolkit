using LocalAi.Installer.Core.Releases;

namespace LocalAi.ReleaseSigner;

/// <summary>
/// Checks that the signed manifest describes the release actually being published.
///
/// Three values have to agree and none of them used to be compared. The version is typed into
/// the note filenames, into the publish script and into the tag; the commit is stamped into the
/// manifest as the version directory and is separately whatever the tag ends up pointing at.
/// Every one of those is a value someone types or a step someone performs in an order they
/// remember, and each disagreement is invisible until an installer refuses a package or, worse,
/// installs the wrong one:
///
/// - a manifest naming 0.1.34 published under the 0.1.35 tag hands every installer the older
///   package uri and quietly downgrades it,
/// - a version directory from a pre-merge commit stamps binaries with a tree nobody can check
///   out from the tag,
/// - a package uri built from a different version points installers at an asset that does not
///   exist yet, or at the previous release's.
/// </summary>
public static class ReleaseConsistency
{
    public const string PackageUriPrefix =
        "https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/";

    public static Uri ExpectedPackageUri(ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new Uri($"{PackageUriPrefix}{version}/localai-package.zip");
    }

    /// <summary>
    /// The version directory is the first twelve characters of the commit the release is built
    /// from, which is what the publish script stamps when it is left to derive one.
    /// </summary>
    public static string ExpectedVersionDirectory(string commitSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        var trimmed = commitSha.Trim();
        return trimmed.Length <= 12 ? trimmed : trimmed[..12];
    }

    public static IReadOnlyList<string> Check(
        ReleaseManifest manifest,
        ReleaseVersion version,
        string commitSha)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        var problems = new List<string>();
        if (!string.Equals(manifest.ReleaseVersion, version.ToString(), StringComparison.Ordinal))
        {
            problems.Add(
                $"The manifest is for {manifest.ReleaseVersion}, not {version}. " +
                "Publishing it under this tag would hand installers the wrong package.");
        }

        var expectedDirectory = ExpectedVersionDirectory(commitSha);
        if (!string.Equals(
                manifest.VersionDirectory,
                expectedDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                $"The manifest was built from {manifest.VersionDirectory}, and this release " +
                $"would be tagged at {expectedDirectory}. The published binaries would come " +
                "from a tree the tag does not name.");
        }

        var expectedUri = ExpectedPackageUri(version);
        if (manifest.PackageUri != expectedUri)
        {
            problems.Add(
                $"The manifest points installers at {manifest.PackageUri}, and this release " +
                $"publishes {expectedUri}.");
        }

        return problems.AsReadOnly();
    }
}
