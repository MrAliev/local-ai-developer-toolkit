using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using LocalAi.Broker;

#pragma warning disable CA1416

namespace LocalAi.Broker.Tests;

public sealed class RuntimeAclTests
{
    [Fact]
    public void Ensure_ignores_a_job_directory_moved_during_acl_application()
    {
        using var root = new TemporaryRuntimeRoot();
        foreach (var child in new[] { "jobs", "archive", "staging", "quarantine" })
        {
            Directory.CreateDirectory(Path.Combine(root.Path, child));
        }

        var source = Path.Combine(root.Path, "jobs", "job-1");
        var destination = Path.Combine(root.Path, "archive", "job-1");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "request.json"), "{}");
        var moved = false;
        var userSid = WindowsIdentity.GetCurrent().User!.Value;
        var acl = new RuntimeAcl(
            isWindows: true,
            currentUser: userSid,
            applyExactAcl: (path, isDirectory, createNew, user, administrators) =>
            {
                if (!moved &&
                    string.Equals(path, source, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(source, destination);
                    moved = true;
                    throw new InvalidOperationException(
                        "Method failed with unexpected error code 3.");
                }

                ApplyRealAcl(path, isDirectory, createNew, user, administrators);
            },
            readAclSnapshot: ReadRealAclSnapshot);

        acl.Ensure(root.Path);

        Assert.True(moved);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public void Ensure_propagates_acl_failure_while_the_target_still_exists()
    {
        using var root = new TemporaryRuntimeRoot();
        foreach (var child in new[] { "jobs", "archive", "staging", "quarantine" })
        {
            Directory.CreateDirectory(Path.Combine(root.Path, child));
        }

        var target = Path.Combine(root.Path, "jobs", "job-1");
        Directory.CreateDirectory(target);
        var userSid = WindowsIdentity.GetCurrent().User!.Value;
        var acl = new RuntimeAcl(
            isWindows: true,
            currentUser: userSid,
            applyExactAcl: (path, isDirectory, createNew, user, administrators) =>
            {
                if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("ACL write failed.");
                }

                ApplyRealAcl(path, isDirectory, createNew, user, administrators);
            },
            readAclSnapshot: ReadRealAclSnapshot);

        var error = Assert.Throws<InvalidOperationException>(
            () => acl.Ensure(root.Path));

        Assert.Equal("ACL write failed.", error.Message);
        Assert.True(Directory.Exists(target));
    }

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

    /// <summary>
    /// The blast-radius half of #200: a junction inside the runtime tree — creatable by the
    /// same unprivileged user who owns the root — used to lead the ACL repair outside it.
    /// The walk must refuse the reparse point by name and never touch what it points at.
    /// </summary>
    [Fact]
    public void Ensure_refuses_a_junction_and_leaves_its_target_untouched()
    {
        using var root = new TemporaryRuntimeRoot();
        using var outside = new TemporaryRuntimeRoot();
        File.WriteAllText(Path.Combine(outside.Path, "sentinel.txt"), "alive");
        Directory.CreateDirectory(Path.Combine(root.Path, "jobs"));
        var junction = Path.Combine(root.Path, "jobs", "detour");
        CreateJunction(junction, outside.Path);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => new RuntimeAcl().Ensure(root.Path));

            Assert.Contains("reparse point", error.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(outside.Path, "sentinel.txt")));
            // The external tree keeps its ordinary inherited ACL: the exact-ACL repair,
            // which always protects what it touches, never followed the junction.
            Assert.False(
                new DirectoryInfo(outside.Path).GetAccessControl().AreAccessRulesProtected);
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    /// <summary>
    /// The cycle half of #200. NTFS cannot form a directory cycle without a reparse point,
    /// so refusing every reparse point is also what keeps this walk finite.
    /// </summary>
    [Fact]
    public void Ensure_refuses_a_junction_cycle()
    {
        using var root = new TemporaryRuntimeRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "jobs"));
        var junction = Path.Combine(root.Path, "jobs", "loop");
        CreateJunction(junction, root.Path);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => new RuntimeAcl().Ensure(root.Path));

            Assert.Contains("reparse point", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // cmd's mklink /J: junction creation needs no privilege, unlike symlinks, which is
        // exactly why the walk has to defend against it.
        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0 && Directory.Exists(link),
            $"Could not create a junction at '{link}': " + process.StandardError.ReadToEnd());
    }

    /// <summary>
    /// Removes the link itself, never its target. cmd's rmdir deletes a junction without
    /// recursing into it, which managed recursive deletion refuses to do here.
    /// </summary>
    private static void RemoveJunction(string link)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("rmdir");
        start.ArgumentList.Add(link);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.False(
            Directory.Exists(link),
            $"Could not remove the junction at '{link}': " +
            process.StandardError.ReadToEnd());
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
