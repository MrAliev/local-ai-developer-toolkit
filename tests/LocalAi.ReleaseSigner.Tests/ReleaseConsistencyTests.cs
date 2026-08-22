using LocalAi.Installer.Core.Releases;
using LocalAi.ReleaseSigner;
using System.Text.Json;

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

    /// <summary>
    /// The manifest published for 0.1.35, byte for byte as the signer wrote it.
    ///
    /// The document is produced by a canonical writer rather than by the serializer that reads it
    /// back here, so nothing but this proves the two agree. A reader that quietly defaulted a
    /// field it could not match would make every comparison above pass for the wrong reason.
    /// </summary>
    [Fact]
    public void A_manifest_this_project_published_reads_back_field_for_field()
    {
        var manifest = ReleaseConsistency.ParseManifest(Published);

        Assert.Equal("0.1.35", manifest.ReleaseVersion);
        Assert.Equal("cfa6449df663", manifest.VersionDirectory);
        Assert.Equal(235178344, manifest.PackageSize);
        Assert.Equal(
            ReleaseConsistency.ExpectedPackageUri(ReleaseVersion.Parse("0.1.35")),
            manifest.PackageUri);
        // This document is also the record of the defect: 0.1.35 was published with an empty
        // model list, like every release from 0.1.29 to 0.1.44, so the installer set up all
        // the binaries and not one model. The consistency check now says so.
        Assert.Equal(
            ["The manifest carries no models"],
            ReleaseConsistency.Check(manifest, ReleaseVersion.Parse("0.1.35"), Commit)
                .Select(problem => problem.Split(',')[0]));
    }

    /// <summary>
    /// The installer's verifier requires exactly the documented properties. A reader here that
    /// tolerated an extra one would call the release consistent right up to the moment nobody
    /// could install it.
    /// </summary>
    [Fact]
    public void A_manifest_carrying_an_undefined_field_is_refused()
    {
        Assert.ThrowsAny<JsonException>(() => ReleaseConsistency.ParseManifest(
            Published.Replace("\"Models\":[]", "\"Models\":[],\"Signed\":true", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The one field that is wrong by omission rather than by disagreement, and the reason it
    /// went unnoticed for sixteen releases: an empty model list contradicts nothing, verifies
    /// correctly and publishes cleanly. It just installs no model.
    /// </summary>
    [Fact]
    public void A_manifest_without_models_stops_the_release()
    {
        var problems = ReleaseConsistency.Check(
            Manifest("0.1.35", "cfa6449df663", "0.1.35", models: []),
            ReleaseVersion.Parse("0.1.35"),
            Commit);

        Assert.Contains(
            problems,
            problem => problem.Contains("carries no models", StringComparison.Ordinal));
    }

    private const string Published =
        """
        {"SchemaVersion":1,"ReleaseVersion":"0.1.35","VersionDirectory":"cfa6449df663","ModelCatalogVersion":"1","ProtocolVersion":1,"BuildCompatibilityId":"localai-broker-v1","PackageUri":"https://github.com/MrAliev/local-ai-developer-toolkit/releases/download/0.1.35/localai-package.zip","PackageSize":235178344,"PackageSha256":"CD8909A6DD901D1E6ABF7008BF464D37A32ADB8B90B1C2E12F668E1F12B68A0E","RequiresAuthenticode":false,"Models":[]}
        """;

    /// <summary>
    /// Carries a model by default, because a release without one is its own problem and would
    /// otherwise be reported alongside every version disagreement these tests are about.
    /// </summary>
    private static ReleaseManifest Manifest(
        string releaseVersion,
        string versionDirectory,
        string packageUriVersion,
        IReadOnlyList<ManifestModel>? models = null) =>
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
            models: models ?? [new ManifestModel("qwen3-embedding:8b-q8_0", 8192, 1024, 1024)]);
}
