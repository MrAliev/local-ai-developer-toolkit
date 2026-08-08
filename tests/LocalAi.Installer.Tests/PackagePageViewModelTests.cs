using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// "Latest" has to keep meaning latest.
///
/// Resolving it once and writing the answer back into the request turned a standing instruction
/// into a fixed one: a wizard opened before a release and used after it installed the version
/// that had been newest when the window opened, while the field still read "latest".
/// </summary>
public sealed class PackagePageViewModelTests
{
    [Fact]
    public void Resolving_latest_does_not_replace_the_request_with_the_answer()
    {
        var page = new PackagePageViewModel();
        Assert.True(page.WantsLatest);

        page.SelectResolvedRelease(Release("0.1.29"), "0.1.29");

        Assert.Equal(PackagePageViewModel.LatestTag, page.ReleaseVersion);
        Assert.True(page.WantsLatest);
        // The resolved release is still named — the request and the answer are simply not the
        // same field any more.
        Assert.Equal("0.1.29", page.ResolvedTag);
        Assert.Contains("0.1.29", page.ReviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolving_again_can_move_to_a_newer_release()
    {
        var page = new PackagePageViewModel();
        page.SelectResolvedRelease(Release("0.1.29"), "0.1.29");

        page.SelectResolvedRelease(Release("0.1.30"), "0.1.30");

        Assert.True(page.WantsLatest);
        Assert.Equal("0.1.30", page.ResolvedTag);
        Assert.Contains("0.1.30", page.ReviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_asked_for_by_name_is_not_treated_as_latest()
    {
        var page = new PackagePageViewModel { ReleaseVersion = "0.1.28" };

        page.SelectResolvedRelease(Release("0.1.28"), "0.1.28");

        // Naming a release is a decision to install that one; re-resolving would quietly
        // override it.
        Assert.False(page.WantsLatest);
        Assert.Equal("0.1.28", page.ReleaseVersion);
    }

    [Fact]
    public void Editing_the_tag_drops_the_release_resolved_for_the_previous_one()
    {
        var page = new PackagePageViewModel();
        page.SelectResolvedRelease(Release("0.1.29"), "0.1.29");

        page.ReleaseVersion = "0.1.27";

        Assert.Null(page.Resolved);
        Assert.Null(page.ResolvedTag);
        Assert.Equal(PackageSourceState.NotChecked, page.State);
    }

    [Fact]
    public void Reset_returns_to_asking_for_the_newest()
    {
        var page = new PackagePageViewModel { ReleaseVersion = "0.1.28" };
        page.SelectResolvedRelease(Release("0.1.28"), "0.1.28");

        page.Reset();

        Assert.True(page.WantsLatest);
        Assert.Null(page.Resolved);
        Assert.Null(page.ResolvedTag);
    }

    [Fact]
    public void A_release_that_is_already_installed_says_so_before_the_run_starts()
    {
        var page = new PackagePageViewModel
        {
            InstalledVersionDirectory = "0123456789ab",
        };

        page.SelectResolvedRelease(Release("0.1.30"), "0.1.30");

        // The installer handles this correctly and reports AlreadyInstalled — afterwards, in a
        // line of the finish log. A run that was never going to change anything then looks
        // exactly like one that did until it is over.
        Assert.True(page.IsAlreadyInstalled);
        Assert.Contains("already installed", page.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing will change", page.ReviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_release_is_not_reported_as_installed()
    {
        var page = new PackagePageViewModel
        {
            InstalledVersionDirectory = "ffffffffffff",
        };

        page.SelectResolvedRelease(Release("0.1.30"), "0.1.30");

        Assert.False(page.IsAlreadyInstalled);
        Assert.Contains("MB to download", page.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_with_nothing_installed_is_not_reported_as_up_to_date()
    {
        var page = new PackagePageViewModel();

        page.SelectResolvedRelease(Release("0.1.30"), "0.1.30");

        Assert.False(page.IsAlreadyInstalled);
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
                packageSize: 1024,
                packageSha256: new string('a', 64),
                requiresAuthenticode: false,
                models: []),
            [1],
            [2]);
}
