using System.Security.AccessControl;
using System.Security.Principal;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The installation root is shared with the broker runtime, and the installer owns only part
/// of it. These pin the two mistakes that made a real installation refuse itself.
/// </summary>
public sealed class InstallationLayoutSharedRootTests : IDisposable
{
    private readonly string localAppData = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.LayoutTests",
        Guid.NewGuid().ToString("N"));

    public InstallationLayoutSharedRootTests() =>
        Directory.CreateDirectory(localAppData);

    public void Dispose()
    {
        try
        {
            Directory.Delete(localAppData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InstallationLayout Layout() =>
        InstallationLayout.FromLocalAppData(localAppData);

    [Fact]
    public void Runtime_files_in_the_root_pass_the_shape_check()
    {
        var layout = Layout();
        Directory.CreateDirectory(layout.Root);
        // Exactly what the broker leaves next to the installation.
        foreach (var name in new[] { "host.json", "policy.json", "sequence.json", "broker.lock" })
        {
            File.WriteAllText(Path.Combine(layout.Root, name), "{}");
        }

        Directory.CreateDirectory(Path.Combine(layout.Root, "jobs"));

        // Acquiring the full lease also demands protected ACLs, which a temp directory does
        // not have, so this asserts on the shape check specifically: it must no longer be
        // the thing that refuses a root shared with the runtime.
        var error = Record.Exception(
            () => InstallationLayoutLease.Acquire(layout, requireFreshInstallerTree: true));

        Assert.DoesNotContain(
            "ValidateRootShape",
            error?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_acl_stricter_than_required_is_accepted()
    {
        // Exactly what the broker writes on the shared root: the user and Administrators,
        // no SYSTEM. Fewer principals than the installer grants itself is not a weaker
        // directory, and demanding an exact match refused every machine the runtime had
        // ever touched.
        var layout = Layout();
        Directory.CreateDirectory(layout.Root);
        var security = new DirectorySecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(layout.Root).SetAccessControl(security);
        File.WriteAllText(Path.Combine(layout.Root, "host.json"), "{}");

        using var lease = InstallationLayoutLease.Acquire(
            layout,
            requireFreshInstallerTree: true);

        Assert.Equal(layout.Root, lease.Layout.Root);
    }

    [Fact]
    public void An_unexpected_folder_in_the_installer_directory_is_still_refused()
    {
        // This is the rule that rejected the installer's own download folder. It must keep
        // rejecting strangers: the directory holds backups the rollback path depends on.
        var layout = Layout();
        Directory.CreateDirectory(layout.InstallerDirectory);
        Directory.CreateDirectory(Path.Combine(layout.InstallerDirectory, "downloads"));

        Assert.Throws<LocalAiPackageInstallationException>(
            () => InstallationLayoutLease.Acquire(layout, requireFreshInstallerTree: true));
    }
}
