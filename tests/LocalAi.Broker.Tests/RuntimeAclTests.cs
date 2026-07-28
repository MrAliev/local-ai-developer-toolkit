using System.Security.AccessControl;
using System.Security.Principal;
using LocalAi.Broker;

#pragma warning disable CA1416

namespace LocalAi.Broker.Tests;

public sealed class RuntimeAclTests
{
    [Fact]
    public void Ensure_applies_and_validates_each_path_in_the_same_pass()
    {
        using var root = new TemporaryRuntimeRoot();
        // Pre-create the standard subdirectories so EnsureDirectory() (which only applies an ACL
        // to directories it creates itself) is a no-op here, and every apply/validate call
        // recorded below comes from the single unified walk this test is actually about.
        foreach (var child in new[] { "jobs", "archive", "staging", "quarantine" })
        {
            Directory.CreateDirectory(Path.Combine(root.Path, child));
        }

        File.WriteAllText(Path.Combine(root.Path, "jobs", "placeholder.txt"), "x");

        var callOrder = new List<string>();
        var userSid = WindowsIdentity.GetCurrent().User!.Value;

        var acl = new RuntimeAcl(
            isWindows: true,
            currentUser: userSid,
            applyExactAcl: (path, isDirectory, createNew, user, administrators) =>
            {
                callOrder.Add("apply:" + path);
                ApplyRealAcl(path, isDirectory, createNew, user, administrators);
            },
            readAclSnapshot: path =>
            {
                callOrder.Add("validate:" + path);
                return ReadRealAclSnapshot(path);
            });

        // Should not throw: every applied path is immediately re-validated before moving on,
        // so a path created by someone else after this call finishes can never be caught
        // mid-way between an "apply everywhere" pass and a separate "validate everywhere" pass.
        acl.Ensure(root.Path);

        Assert.True(callOrder.Count >= 2);
        Assert.Equal(0, callOrder.Count % 2);
        for (var i = 0; i < callOrder.Count; i += 2)
        {
            var path = callOrder[i]["apply:".Length..];
            Assert.Equal("apply:" + path, callOrder[i]);
            Assert.Equal(
                "validate:" + path,
                callOrder[i + 1]);
        }
    }

    [Fact]
    public void Ensure_on_real_filesystem_does_not_throw_for_a_freshly_created_root()
    {
        using var root = new TemporaryRuntimeRoot();

        new RuntimeAcl().Ensure(root.Path);
    }

    [Fact]
    public void Ensure_is_idempotent_when_called_twice_in_a_row()
    {
        using var root = new TemporaryRuntimeRoot();
        var acl = new RuntimeAcl();

        acl.Ensure(root.Path);
        acl.Ensure(root.Path);
    }

    private static void ApplyRealAcl(
        string path,
        bool isDirectory,
        bool createNew,
        string currentUserSid,
        string administratorsSid)
    {
        var user = new SecurityIdentifier(currentUserSid);
        var administrators = new SecurityIdentifier(administratorsSid);
        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            var directory = new DirectoryInfo(path);
            if (createNew)
            {
                directory.Create(security);
            }
            else
            {
                directory.SetAccessControl(security);
            }

            return;
        }

        if (createNew)
        {
            throw new InvalidOperationException("Runtime ACL cannot create a file node.");
        }

        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(fileSecurity);
    }

    private static RuntimeAclSnapshot ReadRealAclSnapshot(string path)
    {
        var isDirectory = Directory.Exists(path);
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        var entries = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new RuntimeAclEntry(
                rule.IdentityReference.Value,
                rule.FileSystemRights,
                rule.AccessControlType,
                rule.InheritanceFlags,
                rule.PropagationFlags,
                rule.IsInherited))
            .ToArray();
        return new RuntimeAclSnapshot(
            isDirectory,
            security.AreAccessRulesProtected,
            entries);
    }
}
