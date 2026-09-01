using LocalAi.Contracts.Activation;
using LocalAi.TestFixtures;
using LocalAi.Installer.ViewModels;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The first sentence of the first screen read "LocalAi 467ed5f0f9bf is installed on this
/// computer." That is the version *directory* — the start screen took it straight from the
/// pointer and never asked which release it came from, so it showed an internal identifier
/// where a person expects a version, while the very next window said 0.1.51.
///
/// The tests did not catch it because the fixture names its version directory "0.1.50", which
/// is release-shaped by coincidence. A check built on data that happens to look right is how
/// this shipped.
/// </summary>
[Collection(InstallerLanguageCollection.Name)]
public sealed class StartScreenNamesTheReleaseTests : IDisposable
{
    // The language is process state now, so a class that asserts English says so. Run
    // after one that chose Russian, it would otherwise read that choice as its own — which
    // is exactly how this first failed.
    // xunit builds one instance per test, so this runs before each of them — a
    // static constructor runs once and lets whichever class went first decide.
    public StartScreenNamesTheReleaseTests() => InstallerCulture.Current = InstallerLanguage.English;


    public void Dispose() => machine.Dispose();

    private const string BuildId = "467ed5f0f9bf";

    [Fact]
    public void When_the_release_is_recorded_the_headline_names_it()
    {
        var start = Start(new InstalledVersion(BuildId, "0.1.51"));

        Assert.Contains("LocalAi 0.1.51 is installed", start.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain(BuildId, start.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// An installation from before the release record existed cannot say which release it is.
    /// The headline says what it knows — that something is installed — rather than answering a
    /// question nobody asked with a hash.
    /// </summary>
    [Fact]
    public void When_the_release_is_unknown_the_headline_carries_no_identifier()
    {
        var start = Start(new InstalledVersion(BuildId, null));

        Assert.Equal("LocalAi is installed on this computer.", start.Headline);
    }

    /// <summary>
    /// The build id is still on the page, two lines down and labelled as a build — somebody has
    /// to be able to answer "which one are you running". Labelled, because an unlabelled hash is
    /// exactly what read as a version.
    /// </summary>
    [Fact]
    public void The_build_is_named_below_when_the_release_is_not_known()
    {
        var start = Start(new InstalledVersion(BuildId, null));

        Assert.Contains("Build " + BuildId, start.Detail, StringComparison.Ordinal);
        Assert.Contains("does not record which release", start.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// And is absent when the release is known: two identifiers for one thing is the confusion
    /// being removed, not a second helping of it.
    /// </summary>
    [Fact]
    public void The_build_is_not_named_when_the_release_is_known()
    {
        var start = Start(new InstalledVersion(BuildId, "0.1.51"));

        Assert.DoesNotContain(BuildId, start.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Build ", start.Detail, StringComparison.Ordinal);
    }

    /// <summary>The row explaining why Install is off follows the same rule.</summary>
    [Theory]
    [InlineData("0.1.51", "LocalAi 0.1.51 is already installed")]
    [InlineData(null, "LocalAi is already installed")]
    public void The_install_row_says_what_it_knows(string? release, string expected)
    {
        var start = Start(new InstalledVersion(BuildId, release));

        Assert.StartsWith(
            expected,
            start.Option(StartChoice.Install).UnavailableReason,
            StringComparison.Ordinal);
    }

    private readonly RemovalFixture machine = new();

    private InstallerStartViewModel Start(InstalledVersion installed) =>
        new(machine.LocalAppData, readInstalledVersion: () => installed);
}
