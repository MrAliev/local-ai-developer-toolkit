using LocalAi.Installer.Core;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The guard that makes "every test starts in English" structural rather than a convention each
/// new test class has to remember.
///
/// What this pins is the guard's own promise: it sets English, it does not restore whatever was
/// there before. Restoring would be the natural-looking change that quietly puts the convention
/// back, because a test that ran after a Russian one would then start in Russian again.
///
/// What it deliberately does not pin is that xunit calls it — that is the assembly attribute's
/// contract, and the run order of two classes is not something a test can arrange.
/// </summary>
public sealed class PinInstallerLanguageTests
{
    [Fact]
    public void The_guard_puts_the_language_back_to_english()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        new PinInstallerLanguageAttribute().Before(methodUnderTest: null!, test: null!);

        Assert.Equal(InstallerLanguage.English, InstallerCulture.Current);
    }
}
