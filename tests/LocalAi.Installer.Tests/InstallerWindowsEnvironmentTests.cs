namespace LocalAi.Installer.Tests;

public sealed class InstallerWindowsEnvironmentTests
{
    [Fact]
    public void Missing_windir_is_recovered_from_system_directory()
    {
        var resolved = InstallerWindowsEnvironment.ResolveWindowsDirectory(
            null,
            @"C:\Windows\System32");

        Assert.Equal(@"C:\Windows", resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-windows")]
    public void Invalid_windir_is_recovered_from_system_directory(string configured)
    {
        var resolved = InstallerWindowsEnvironment.ResolveWindowsDirectory(
            configured,
            @"D:\Windows\System32");

        Assert.Equal(@"D:\Windows", resolved);
    }

    [Fact]
    public void Absolute_windir_is_preserved()
    {
        var resolved = InstallerWindowsEnvironment.ResolveWindowsDirectory(
            @"E:\CustomWindows\",
            @"C:\Windows\System32");

        Assert.Equal(@"E:\CustomWindows", resolved);
    }
}
