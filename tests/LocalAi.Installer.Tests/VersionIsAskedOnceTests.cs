using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// One run asked about versions three times, in three vocabularies: the start screen offered to
/// install "the release you choose", the package page had a "Release tag" box and a "Check
/// release" button, and the confirm page had "Check for new LocalAi releases" — which is consent
/// for a later background check, not a question about this run (#257).
///
/// The page resolves on its own now, so the button is no longer how an answer arrives. These pin
/// the wording to what actually happens, and the plumbing that has to exist for the wording to
/// be true.
/// </summary>
public sealed class VersionIsAskedOnceTests
{
    /// <summary>
    /// The status line, the button and the progress indicator all read this, so a change that
    /// nothing is told about leaves a page saying "checking…" over a finished check.
    /// </summary>
    [Fact]
    public void Resolving_announces_itself_so_the_page_can_show_it()
    {
        var page = new PackagePageViewModel();
        var announced = new List<string>();
        page.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        page.BeginResolving();

        Assert.Contains(nameof(PackagePageViewModel.IsResolving), announced);
        Assert.Contains(nameof(PackagePageViewModel.CanCheck), announced);
        Assert.True(page.IsResolving);
        Assert.False(page.CanCheck);
    }

    [Fact]
    public void A_finished_check_lets_the_button_be_pressed_again()
    {
        var page = new PackagePageViewModel();
        page.BeginResolving();

        page.EndResolving();

        Assert.True(page.CanCheck);
    }

    /// <summary>
    /// A failure used to land in the same muted grey as a success, so the page read as blank
    /// rather than as a state with a way out. The reason still comes from the feed — it carries
    /// its own remedy — but it is now announced as a failure first.
    /// </summary>
    [Fact]
    public void A_failed_lookup_says_so_before_it_says_why()
    {
        var page = new PackagePageViewModel();

        page.ReportUnavailable("Check this computer's internet connection, then try again.");

        Assert.StartsWith("No release resolved.", page.StatusText, StringComparison.Ordinal);
        Assert.Contains("internet connection", page.StatusText, StringComparison.Ordinal);
        Assert.Equal(PackageSourceState.Unavailable, page.State);
    }

    /// <summary>
    /// Before anything has been looked up, the line has to name the way forward. "No release has
    /// been checked yet" describes a hole; a person reading it does not learn what to press.
    /// </summary>
    [Fact]
    public void An_unchecked_page_names_the_way_forward()
    {
        Assert.Contains("Check again", new PackagePageViewModel().StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// `latest` is not a version: it resolves again when Install is pressed. The review line
    /// promised a specific one, so the page that is meant to be a complete list of effects could
    /// name a version other than the one installed.
    /// </summary>
    [Fact]
    public void A_latest_request_says_that_the_answer_can_still_move()
    {
        var page = new PackagePageViewModel { ReleaseVersion = "latest" };
        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Contains("whatever is newest", page.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pinned tag is a promise the run can keep, so it reads as one.
    /// </summary>
    [Fact]
    public void A_pinned_version_is_stated_plainly()
    {
        var page = new PackagePageViewModel { ReleaseVersion = "0.1.50" };
        page.SelectResolvedRelease(Release("0.1.50"), "0.1.50");

        Assert.DoesNotContain("whatever is newest", page.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The button on the page that carries out the run is the most-read control there. "Install"
    /// on a run somebody started as "Update or repair" is the "did it hear me?" this issue is
    /// about.
    /// </summary>
    [Theory]
    [InlineData(StartChoice.Install, "Install")]
    [InlineData(StartChoice.UpdateOrRepair, "Update")]
    public void The_action_names_the_errand(StartChoice mode, string expected)
    {
        Assert.Equal(expected, new InstallerWizardViewModel(mode).ActionText);
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
}
