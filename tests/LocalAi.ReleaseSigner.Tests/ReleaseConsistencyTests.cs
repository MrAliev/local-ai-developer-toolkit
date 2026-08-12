using LocalAi.Installer.Core.Releases;
using LocalAi.ReleaseSigner;

namespace LocalAi.ReleaseSigner.Tests;

/// <summary>
/// The version has to be the same in four places and the commit in two, and until now nothing
/// compared them. These are the three ways they can disagree.
/// </summary>
public sealed class ReleaseConsistencyTests
{
    private const string Commit = "cfa6449df66317fcc22d5c5e3a030df949de8c7f";

    [Fact]
    public void A_manifest_that_agrees_has_nothing_to_report()
    {
        var problems = ReleaseConsistency.Check(
            Manifest("0.1.35", "cfa6449df663", "0.1.35"),
            ReleaseVersion.Parse("0.1.35"),
            Commit);

        Assert.Empty(problems);
    }

    /// <summary>
    /// The shape of a build run before the version was bumped. Published under the new tag it
    /// hands every installer the previous release's package uri, which is a downgrade that
    /// verifies correctly and installs cleanly.
    /// </summary>
    [Fact]
    public void A_manifest_built_for_another_version_is_named_as_such()
    {
        // Only the release version disagrees. Giving the manifest the older package uri too
        // would let the uri check answer for this one, and dropping the version comparison
        // altogether would go unnoticed.
        var problems = ReleaseConsistency.Check(
            Manifest("0.1.34", "cfa6449df663", "0.1.35"),
            ReleaseVersion.Parse("0.1.35"),
            Commit);

        Assert.Contains(
            problems,
            problem => problem.Contains("manifest is for 0.1.34", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shape of a build run before the release pull request merged: the binaries carry a
    /// commit that the tag does not name, so the tree they came from cannot be checked out from
    /// the release.
    /// </summary>
    [Fact]
    public void A_manifest_built_from_another_commit_is_named_as_such()
    {
        var problems = ReleaseConsistency.Check(
            Manifest("0.1.35", "0123456789ab", "0.1.35"),
            ReleaseVersion.Parse("0.1.35"),
            Commit);

        Assert.Contains(
            problems,
            problem => problem.Contains("0123456789ab", StringComparison.Ordinal));
    }

    [Fact]
    public void A_package_uri_for_another_release_is_named_as_such()
    {
        var problems = ReleaseConsistency.Check(
            Manifest("0.1.35", "cfa6449df663", "0.1.30"),
            ReleaseVersion.Parse("0.1.35"),
            Commit);

        Assert.Contains(
            problems,
            problem => problem.Contains("0.1.30/localai-package.zip", StringComparison.Ordinal));
    }

    private static ReleaseManifest Manifest(
        string releaseVersion,
        string versionDirectory,
        string packageUriVersion) =>
        new(
            schemaVersion: 1,
            releaseVersion: releaseVersion,
            versionDirectory: versionDirectory,
            modelCatalogVersion: "1",
            protocolVersion: 1,
            buildCompatibilityId: "localai-broker-v1",
            packageUri: new Uri(
                $"{ReleaseConsistency.PackageUriPrefix}{packageUriVersion}/localai-package.zip"),
            packageSize: 1024,
            packageSha256: new string('A', 64),
            requiresAuthenticode: false,
            models: []);
}
