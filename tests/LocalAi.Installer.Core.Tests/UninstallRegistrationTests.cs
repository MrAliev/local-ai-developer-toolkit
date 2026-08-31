using LocalAi.Installer.Core.Removal;
using LocalAi.TestFixtures;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The entry in Apps &amp; features, and the copy of the installer it points at. Removal is
/// only a first-class way out if it can be found where people look for it.
///
/// These write to a key of their own under HKCU rather than the real Uninstall key: a test
/// suite that registers a product on the machine running it, or removes one that is genuinely
/// installed there, would be doing something nobody asked for.
/// </summary>
public sealed class UninstallRegistrationTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    private readonly string subKey =
        @"Software\LocalAi.Tests\" + Guid.NewGuid().ToString("N") + @"\Uninstall\LocalAi";

    private readonly string installerSource = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.RegistrationTests",
        Guid.NewGuid().ToString("N"),
        "LocalAi.Installer.exe");

    public UninstallRegistrationTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installerSource)!);
        File.WriteAllText(installerSource, "installer");
    }

    public void Dispose()
    {
        machine.Dispose();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\LocalAi.Tests\" + subKey.Split('\\')[2],
                throwOnMissingSubKey: false);
            Directory.Delete(Path.GetDirectoryName(installerSource)!, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    [Fact]
    public void Installing_writes_the_entry_and_the_copy_it_points_at()
    {
        var registration = Registration();

        var entry = registration.Register("0.1.50", installerSource);

        Assert.True(File.Exists(registration.UninstallerPath));
        Assert.Equal("LocalAi Developer Toolkit", entry.DisplayName);
        Assert.Equal("0.1.50", entry.DisplayVersion);
        Assert.Equal(machine.Runtime, entry.InstallLocation);
        Assert.Equal(
            "\"" + registration.UninstallerPath + "\" --uninstall",
            entry.UninstallString);
        // Estimated from what is actually installed rather than guessed: this installation is
        // mostly its indexes, which differ by an order of magnitude between machines.
        Assert.True(entry.EstimatedSizeKilobytes >= 0);
        Assert.Equal(entry, registration.Read());
    }

    [Fact]
    public void The_entry_offers_no_repair_or_modify_it_cannot_perform()
    {
        Registration().Register("0.1.50", installerSource);

        using var key = Registry.CurrentUser.OpenSubKey(subKey);

        Assert.Equal(1, key!.GetValue("NoModify"));
        Assert.Equal(1, key.GetValue("NoRepair"));
    }

    /// <summary>
    /// The key's name is the product, not the version, so an upgrade updates the one entry
    /// instead of leaving a list of every release ever installed.
    /// </summary>
    [Fact]
    public void An_upgrade_rewrites_the_version_rather_than_adding_a_second_entry()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);

        registration.Register("0.1.51", installerSource);

        Assert.Equal("0.1.51", registration.Read()!.DisplayVersion);
        using var parent = Registry.CurrentUser.OpenSubKey(
            subKey[..subKey.LastIndexOf('\\')]);
        Assert.Equal(["LocalAi"], parent!.GetSubKeyNames());
    }

    [Fact]
    public void Uninstalling_takes_the_entry_out_and_says_so_only_once()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);

        Assert.True(registration.Unregister());

        Assert.Null(registration.Read());
        // An entry somebody already removed by hand is not a failure to report.
        Assert.False(registration.Unregister());
    }

    [Fact]
    public void The_uninstallers_copy_goes_when_nothing_is_holding_it()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);

        Assert.True(registration.RemoveUninstallerCopy());

        Assert.False(Directory.Exists(registration.UninstallerDirectory));
        // Removing it twice is what a second uninstall run would do.
        Assert.True(registration.RemoveUninstallerCopy());
    }

    /// <summary>
    /// The ordinary case when Apps &amp; features started the uninstaller: Windows will not
    /// delete the executable it is running, so the directory is handed to something that
    /// outlives this process, and the run says the folder goes in a moment rather than leaving
    /// it to be found later and wondered about.
    /// </summary>
    [Fact]
    public void A_copy_that_is_still_running_is_left_to_a_process_that_outlives_it()
    {
        var deferred = new List<string>();
        var registration = Registration(deferred.Add);
        registration.Register("0.1.50", installerSource);
        using var running = new FileStream(
            registration.UninstallerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var removedNow = registration.RemoveUninstallerCopy();

        Assert.False(removedNow);
        Assert.Equal([registration.UninstallerDirectory], deferred);
        Assert.True(File.Exists(registration.UninstallerPath));
    }

    /// <summary>
    /// A repair started from the parked copy would otherwise try to copy the file over itself,
    /// which throws.
    /// </summary>
    [Fact]
    public void Registering_from_the_parked_copy_is_not_a_copy_onto_itself()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);

        var entry = registration.Register("0.1.51", registration.UninstallerPath);

        Assert.Equal("0.1.51", entry.DisplayVersion);
        Assert.True(File.Exists(registration.UninstallerPath));
    }

    private UninstallRegistration Registration(Action<string>? removeAfterExit = null) =>
        new(machine.Layout, subKey, removeAfterExit ?? (_ => { }));
}
