using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The installer offers its own directory as the release source when that directory holds a
/// release — the handover case, where somebody carries the three files to a machine with no
/// route to GitHub. On the update path the package page is folded away, so that offer is
/// accepted without the box that shows it ever being on screen.
///
/// The review page is meant to be a complete list of effects. Naming the version but not where
/// it comes from is how an upgrade installs from a stale folder beside the installer while the
/// person reading believes it came from GitHub (#257).
/// </summary>
public sealed class ReleaseSourceIsVisibleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-source-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_release_taken_from_a_folder_says_so_on_the_review()
    {
        var page = new PackagePageViewModel { SourceFolder = _root };
        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Contains(_root, page.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the ordinary case stays short: naming GitHub on every run would make the line longer
    /// without telling anybody anything they did not assume.
    /// </summary>
    [Fact]
    public void A_release_from_github_does_not_name_a_source()
    {
        var page = new PackagePageViewModel();
        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.DoesNotContain("from ", page.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Including when the folder came from the offer rather than from typing, which is the
    /// path where nobody saw the box at all.
    /// </summary>
    [Fact]
    public void An_offered_folder_is_named_like_any_other()
    {
        Directory.CreateDirectory(_root);
        foreach (var name in new[]
                 {
                     "release-manifest.json", "release-manifest.sig", "localai-package.zip",
                 })
        {
            File.WriteAllText(Path.Combine(_root, name), "x");
        }

        var page = new PackagePageViewModel();
        page.OfferLocalFolder(_root);
        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Contains(_root, page.ReviewText, StringComparison.Ordinal);
    }

    private static ResolvedRelease Release(string version) =>
        new(
            new ReleaseManifest(
                schemaVersion: 1,
                releaseVersion: version,
                versionDirectory: "0123456789ab",
                modelCatalogVersion: "1",
                protocolVersion: 1,
                buildCompatibilityId: "test",
                packageUri: new Uri($"https://example.invalid/{version}/localai-package.zip"),
                packageSize: 208L * 1024 * 1024,
                packageSha256: new string('a', 64),
                requiresAuthenticode: false,
                models: []),
            [1],
            [1]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
