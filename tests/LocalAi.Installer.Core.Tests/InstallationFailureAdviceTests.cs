using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Tests;

public sealed class InstallationFailureAdviceTests
{
    private const string LayoutRefusal =
        "The LocalAi installation layout is unsafe (check: ValidateAcl): the directory still " +
        "inherits access rules.";

    [Fact]
    public void Tells_an_empty_root_to_be_deleted()
    {
        var advice = InstallationFailureAdvice.ForLayoutFailure(
            LayoutRefusal,
            @"C:\Users\me\AppData\Local\LocalAi",
            holdsInstalledVersions: false);

        Assert.NotNull(advice);
        Assert.Contains(@"C:\Users\me\AppData\Local\LocalAi", advice, StringComparison.Ordinal);
        Assert.Contains("Delete that directory", advice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same refusal on a machine with versions installed must never suggest deleting the
    /// tree: it also holds this machine's repository indexes, and rebuilding those costs hours
    /// of somebody's graphics card.
    /// </summary>
    [Fact]
    public void Never_suggests_deleting_a_root_that_holds_versions()
    {
        var advice = InstallationFailureAdvice.ForLayoutFailure(
            LayoutRefusal,
            @"C:\Users\me\AppData\Local\LocalAi",
            holdsInstalledVersions: true);

        Assert.NotNull(advice);
        Assert.DoesNotContain("Delete that directory", advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must not simply be deleted", advice, StringComparison.Ordinal);
        Assert.Contains("indexes", advice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Another LocalAi installation is already in progress.")]
    [InlineData("")]
    [InlineData(null)]
    public void Stays_silent_about_every_other_failure(string? message)
    {
        Assert.Null(InstallationFailureAdvice.ForLayoutFailure(
            message,
            @"C:\Users\me\AppData\Local\LocalAi",
            holdsInstalledVersions: false));
    }
}
