using LocalAi.Contracts;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The same type, asked the same question, in the other globalization mode.
///
/// Directory.Build.props sets InvariantGlobalization for the solution, and there ToUpperInvariant
/// and OrdinalIgnoreCase both collapse to ASCII and cannot disagree. The installer turns it back
/// on, and LocalAi.Contracts ships into that process — so this is the only project where the
/// disagreement is reachable at all. Measured over the BMP, exactly two pairs fold under
/// ToUpperInvariant but not under OrdinalIgnoreCase; the first is U+0053 (S) with U+017F (ſ).
///
/// Defining equality on IdentityKey is what makes the answer the same in both processes. Without
/// it, two paths naming one overlay directory compare unequal here and equal everywhere else.
/// </summary>
public sealed class FsPathUnderFullGlobalizationTests
{
    [Fact]
    public void Two_paths_that_share_an_index_directory_are_the_same_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Case folding is a Windows path rule.");
        Assert.SkipWhen(
            AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) &&
                invariant,
            "This project is expected to run with full globalization; it did not.");

        var longS = FsPath.From("R:" + Path.DirectorySeparatorChar + (char)0x017F);
        var plainS = FsPath.From(@"R:\s");

        // The premise: these two do name one directory, because the key is what names it.
        Assert.Equal(longS.IdentityKey, plainS.IdentityKey);

        Assert.Equal(longS, plainS);
        Assert.Equal(longS.GetHashCode(), plainS.GetHashCode());
    }
}
