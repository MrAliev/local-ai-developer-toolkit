using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The wizard knows which errand the start screen was asked for.
///
/// It did not: `StartWindow` opened the same `MainWindow` for "Install LocalAi" and for
/// "Update or repair", and the view model had no field for the choice — so an update
/// introduced itself as an installation and asked every question an installation asks
/// (#257). Everything else in that issue depends on this one value existing.
/// </summary>
public sealed class WizardErrandTests
{
    [Fact]
    public void An_unstated_errand_is_an_installation()
    {
        Assert.Equal(StartChoice.Install, new InstallerWizardViewModel().Mode);
    }

    [Theory]
    [InlineData(StartChoice.Install)]
    [InlineData(StartChoice.UpdateOrRepair)]
    [InlineData(StartChoice.CleanReinstall)]
    public void The_errand_survives_construction(StartChoice mode)
    {
        Assert.Equal(mode, new InstallerWizardViewModel(mode).Mode);
    }

    /// <summary>
    /// The title bar is the first thing that says what this run is. An update calling itself
    /// "LocalAi Setup" is the same wizard as an install, which is the impression that has to
    /// stop.
    /// </summary>
    [Theory]
    [InlineData(StartChoice.Install, "LocalAi Setup")]
    [InlineData(StartChoice.UpdateOrRepair, "LocalAi — Update or repair")]
    [InlineData(StartChoice.CleanReinstall, "LocalAi — Reinstall")]
    public void The_title_says_what_this_run_is(StartChoice mode, string expected)
    {
        Assert.Equal(expected, new InstallerWizardViewModel(mode).WindowTitle);
    }

    /// <summary>
    /// The version line is present on every page and never empty — a rail that sometimes says
    /// nothing is a rail people stop reading. Its exact content depends on the machine, so
    /// what is pinned here is that it always answers.
    /// </summary>
    [Fact]
    public void The_rail_always_carries_a_version_line()
    {
        var context = new InstallerWizardViewModel(StartChoice.UpdateOrRepair).VersionContext;

        Assert.False(string.IsNullOrWhiteSpace(context));
    }

    /// <summary>
    /// The installed half is read once and never again — not on a page turn, not during the
    /// run, not after it.
    ///
    /// It is a statement about what was here before, and an installation rewrites the version
    /// pointer it comes from, so a second read answers a different question. The first attempt
    /// at this froze the whole line instead, which fixed the finish page and left the install
    /// path saying "checking…" forever.
    /// </summary>
    [Fact]
    public void The_installed_half_is_read_once()
    {
        var reads = 0;
        var wizard = new InstallerWizardViewModel(
            StartChoice.Install,
            () =>
            {
                reads++;
                return "0.1.50";
            });

        var first = wizard.VersionContext;
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        wizard.MoveNext();
        var afterAPageTurn = wizard.VersionContext;

        Assert.StartsWith("0.1.50", first, StringComparison.Ordinal);
        Assert.StartsWith("0.1.50", afterAPageTurn, StringComparison.Ordinal);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// The other half is not history: it is what this run is putting there, and on an
    /// installation it is not known when the window opens. Three states, and the difference
    /// between the first two is the whole point — a check nobody started reads the same as a
    /// check still running only if the wizard cannot tell them apart.
    /// </summary>
    [Fact]
    public void The_errand_half_says_which_of_the_three_it_is()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.Install, () => null);

        Assert.Equal("no release", wizard.VersionContext);

        wizard.Package.BeginResolving();
        Assert.Equal("checking…", wizard.VersionContext);

        wizard.Package.ReportUnavailable("the feed could not be reached");
        Assert.Equal("no release", wizard.VersionContext);
    }

    /// <summary>
    /// Both halves together, on a machine that already has something. The arrow is what says
    /// one version is being replaced by another; without a left half there is nothing to
    /// replace and the line is just the release going in.
    /// </summary>
    [Fact]
    public void The_arrow_appears_only_when_something_is_being_replaced()
    {
        Assert.Equal(
            "no release",
            new InstallerWizardViewModel(StartChoice.Install, () => null).VersionContext);
        Assert.Equal(
            "0.1.50 → no release",
            new InstallerWizardViewModel(StartChoice.Install, () => "0.1.50").VersionContext);
    }

    /// <summary>
    /// Once the run has started the line is stated, not re-read — including after it has
    /// finished, which is where the first attempt at this let go: the guard was on
    /// <c>isRunning</c>, and RunAsync clears that in its finally block before the refresh that
    /// rebuilds the line. So the finish page rebuilt it out of the pointer the run had just
    /// written, and "0.1.50 → 0.1.51" became "0.1.51 → 0.1.51 (repair)" at exactly the moment
    /// somebody was reading the outcome.
    /// </summary>
    [Fact]
    public async Task What_the_run_was_about_survives_the_run()
    {
        var installed = "0.1.50";
        var wizard = new InstallerWizardViewModel(StartChoice.Install, () => installed);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(true);
        foreach (var name in new[]
                 {
                     "Git", "Ollama", "GitHubCli", "DotNetSdk",
                     "NodeJs", "ScipTypeScript", "Python", "ScipPython",
                 })
        {
            wizard.Dependencies.SetInstalled(name, true);
        }

        for (var step = 0; step < 6; step++)
        {
            wizard.MoveNext();
        }

        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
        var before = wizard.VersionContext;
        Assert.StartsWith("0.1.50", before, StringComparison.Ordinal);

        // A dry run: everything is present and no release was chosen, so nothing is installed.
        // The disk moves under the wizard while it runs, the way a real install moves it by
        // writing a new version pointer.
        wizard.SetReviewConfirmed(true);
        installed = "0.1.51";
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(InstallerPage.Finish, wizard.CurrentPage);
        // The run wrote a new pointer; the left half still says what was here before it did.
        Assert.StartsWith("0.1.50 →", wizard.VersionContext, StringComparison.Ordinal);
        Assert.Equal(before, wizard.VersionContext);
    }

    /// <summary>
    /// A verified release survives a re-check that fails, and the installation goes ahead.
    ///
    /// The run re-asks the feed immediately before installing, in case something was published
    /// while the wizard sat open. An attempt to show "checking…" on the rail during that
    /// re-check cleared the verified release first — so a transient network fault turned a
    /// successful install into "no verified release was selected, so nothing was installed",
    /// advice the reader had already followed.
    ///
    /// The first version of this test asserted against the page directly and never ran the
    /// re-check at all, so restoring the defect left it green. It goes through the run now.
    /// </summary>
    [Fact]
    public async Task A_re_check_that_fails_still_installs_what_was_verified()
    {
        var feed = new ScriptedFeed { Release = Release("0.1.51") };
        var wizard = new InstallerWizardViewModel(StartChoice.Install, () => null, feed)
        {
            LogDirectory = TempLog(),
        };
        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        Assert.Equal("0.1.51", wizard.Package.ResolvedTag);

        // From here the feed is unreachable, exactly as a dropped connection would leave it.
        feed.Throw = true;
        var report = new StringBuilder();
        await wizard.RefreshLatestBeforeInstallAsync(
            report,
            TestContext.Current.CancellationToken);

        // What the install step reads two lines later must still be there.
        Assert.NotNull(wizard.Package.Resolved);
        Assert.Equal("0.1.51", wizard.Package.ResolvedTag);
        Assert.False(wizard.Package.IsResolving);
        Assert.Contains("continuing with 0.1.51", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The release is resolved behind the first page, without anybody pressing "Check
    /// release" — which is what lets the rail name it at all on an installation.
    /// </summary>
    [Fact]
    public async Task Initialising_resolves_the_release_on_the_install_path()
    {
        var feed = new ScriptedFeed { Release = Release("0.1.51") };
        var wizard = new InstallerWizardViewModel(StartChoice.Install, () => null, feed)
        {
            LogDirectory = TempLog(),
        };

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, feed.Resolves);
        Assert.Equal("0.1.51", wizard.VersionContext);
    }

    /// <summary>
    /// A resolve overtaken by a newer question must not answer it. The package page is on
    /// screen during an installation and its tag box updates on every keystroke, so two
    /// resolves can be in flight; the slower one arriving last would otherwise put a release
    /// nobody asked for on screen, and install it.
    /// </summary>
    [Fact]
    public async Task A_resolve_that_was_overtaken_does_not_answer()
    {
        var feed = new ScriptedFeed { Release = Release("0.1.44") };
        var wizard = new InstallerWizardViewModel(StartChoice.Install, () => null, feed)
        {
            LogDirectory = TempLog(),
        };

        // The first resolve is held mid-flight while the question changes under it.
        feed.Gate = new TaskCompletionSource();
        var first = wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        wizard.Package.ReleaseVersion = "0.1.51";
        feed.Gate.SetResult();
        await first;

        Assert.Null(wizard.Package.Resolved);
        Assert.Equal("0.1.51", wizard.Package.ReleaseVersion);
        Assert.Equal("no release", wizard.VersionContext);
    }

    private static string TempLog() => Path.Combine(
        Path.GetTempPath(),
        "LocalAi.VersionLine",
        Guid.NewGuid().ToString("N"));

    /// <summary>A wizard standing on the review page, with everything it needs already met.</summary>
    private static async Task<InstallerWizardViewModel> Ready(IReleaseFeed feed)
    {
        var wizard = new InstallerWizardViewModel(StartChoice.Install, () => null, feed)
        {
            LogDirectory = TempLog(),
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(true);
        foreach (var name in new[]
                 {
                     "Git", "Ollama", "GitHubCli", "DotNetSdk",
                     "NodeJs", "ScipTypeScript", "Python", "ScipPython",
                 })
        {
            wizard.Dependencies.SetInstalled(name, true);
        }

        for (var step = 0; step < 6; step++)
        {
            wizard.MoveNext();
        }

        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
        await Task.CompletedTask;
        return wizard;
    }

    /// <summary>A feed that answers what the test tells it to, and can be held or broken.</summary>
    private sealed class ScriptedFeed : IReleaseFeed
    {
        public ResolvedRelease? Release { get; set; }

        public bool Throw { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public int Resolves { get; private set; }

        public async Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct)
        {
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return Throw
                ? throw new ReleaseResolutionException("the feed could not be reached")
                : Release?.Manifest.ReleaseVersion ?? "0.0.0";
        }

        public async Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken ct)
        {
            Resolves++;
            await Task.CompletedTask;
            return Throw
                ? throw new ReleaseResolutionException("the feed could not be reached")
                : Release ?? throw new ReleaseResolutionException("no release");
        }

        /// <summary>
        /// Never reached by these tests: the run stops before downloading, because nothing
        /// here supplies a package to verify.
        /// </summary>
        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct) =>
            throw new NotSupportedException("these tests never reach the download");
    }

    /// <summary>Built the way PackagePageViewModelTests builds one, so the shapes agree.</summary>
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

    /// <summary>
    /// The consent is worded for the run it consents to. "I have reviewed these settings" was
    /// the same sentence on all three errands, and on a reinstall it was the wrong one: what
    /// the person is agreeing to there is a removal followed by an installation, not a set of
    /// settings.
    /// </summary>
    [Theory]
    [InlineData(StartChoice.Install, "installed")]
    [InlineData(StartChoice.UpdateOrRepair, "change")]
    [InlineData(StartChoice.CleanReinstall, "removed")]
    public void The_consent_names_what_this_run_does(StartChoice mode, string expected)
    {
        Assert.Contains(
            expected,
            new InstallerWizardViewModel(mode).ConsentText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// One tick over one list. Two boxes on one page teach people to tick boxes, so the
    /// reinstall consent covers both halves in a single sentence rather than asking twice.
    /// </summary>
    [Fact]
    public void A_reinstall_consents_to_both_halves_at_once()
    {
        var consent = new InstallerWizardViewModel(StartChoice.CleanReinstall).ConsentText;

        Assert.Contains("removed", consent, StringComparison.Ordinal);
        Assert.Contains("installed", consent, StringComparison.Ordinal);
    }
}
