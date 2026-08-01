using System.Security.AccessControl;
using System.Security.Principal;

namespace LocalAi.Broker;

#pragma warning disable CA1416

public sealed class RuntimeAcl
{
    private const string AdministratorsSid = "S-1-5-32-544";

    private readonly bool _isWindows;
    private readonly string? _currentUser;
    private readonly Func<string, string> _normalizeTrustee;
    private readonly Action<string, bool, bool, string, string> _applyExactAcl;
    private readonly Func<string, RuntimeAclSnapshot> _readAclSnapshot;

    public RuntimeAcl(
        bool? isWindows = null,
        string? currentUser = null,
        Action<string, bool, bool, string, string>? applyExactAcl = null,
        Func<string, RuntimeAclSnapshot>? readAclSnapshot = null,
        Func<string, string>? normalizeTrustee = null)
    {
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _currentUser = currentUser;
        _normalizeTrustee = normalizeTrustee ?? NormalizeTrustee;
        _applyExactAcl = applyExactAcl ?? ApplyExactAcl;
        _readAclSnapshot = readAclSnapshot ?? ReadAclSnapshot;
    }

    public void Ensure(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var fullPath = Path.GetFullPath(runtimeRoot);
        if (!_isWindows)
        {
            Directory.CreateDirectory(fullPath);
            Directory.CreateDirectory(Path.Combine(fullPath, "jobs"));
            return;
        }

        var currentUserSid = _normalizeTrustee(_currentUser ?? GetCurrentUser());
        EnsureDirectory(fullPath, currentUserSid);
        foreach (var child in new[] { "jobs", "archive", "staging", "quarantine" })
        {
            EnsureDirectory(Path.Combine(fullPath, child), currentUserSid);
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentUserSid,
            AdministratorsSid
        };

        // Apply and validate each path in the same pass. Two separate full-tree walks (apply
        // everywhere, then re-walk and validate everywhere) leave a window where a path created
        // by a concurrent process (e.g. another Ensure() run, or the broker writing a new job
        // file) between the two walks is picked up by the validate pass but was never touched by
        // the apply pass — it still carries a merely-inherited ACL (protected=false) and fails
        // validation even though nothing is actually wrong.
        foreach (var path in EnumerateRuntimeTree(fullPath))
        {
            ApplyAndValidate(path, currentUserSid, expected);
        }
    }

    private void ApplyAndValidate(
        string path,
        string currentUserSid,
        IReadOnlySet<string> expected)
    {
        try
        {
            var isDirectory = Directory.Exists(path);
            _applyExactAcl(path, isDirectory, false, currentUserSid, AdministratorsSid);
            ValidateSnapshot(path, _readAclSnapshot(path), expected);
        }
        catch (Exception exception) when (
            IsDisappearanceCandidate(exception) &&
            HasDisappeared(path))
        {
            // The broker atomically moves jobs between runtime directories. A path that no longer
            // exists has no ACL left to repair or validate; its destination is covered by the
            // secured runtime tree and will be normalized by a subsequent pass if this traversal
            // already passed it.
        }
    }

    private void EnsureDirectory(string path, string currentUserSid)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        _applyExactAcl(path, true, true, currentUserSid, AdministratorsSid);
    }

    private static void ApplyExactAcl(
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
            var security = BuildDirectorySecurity(user, administrators);
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

        new FileInfo(path).SetAccessControl(BuildFileSecurity(user, administrators));
    }

    private static DirectorySecurity BuildDirectorySecurity(
        SecurityIdentifier user,
        SecurityIdentifier administrators)
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
        return security;
    }

    private static FileSecurity BuildFileSecurity(
        SecurityIdentifier user,
        SecurityIdentifier administrators)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private static IEnumerable<string> EnumerateRuntimeTree(string runtimeRoot)
    {
        yield return runtimeRoot;

        var pending = new Stack<string>();
        pending.Push(runtimeRoot);
        while (pending.TryPop(out var directory))
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                yield return entry;
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static bool IsDisappearanceCandidate(Exception exception) =>
        exception is IOException or InvalidOperationException;

    private static bool HasDisappeared(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static void ValidateSnapshot(
        string path,
        RuntimeAclSnapshot snapshot,
        IReadOnlySet<string> expectedTrustees)
    {
        if (!snapshot.AreAccessRulesProtected || snapshot.Entries.Count != 2)
        {
            throw new InvalidOperationException(
                $"Runtime ACL verification failed for '{path}': " +
                $"protected={snapshot.AreAccessRulesProtected}, entries={snapshot.Entries.Count}.");
        }

        var actualTrustees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedInheritance = snapshot.IsDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        foreach (var entry in snapshot.Entries)
        {
            actualTrustees.Add(entry.SecurityIdentifier);
            if (entry.AccessControlType != AccessControlType.Allow ||
                entry.IsInherited ||
                entry.FileSystemRights != FileSystemRights.FullControl ||
                entry.InheritanceFlags != expectedInheritance ||
                entry.PropagationFlags != PropagationFlags.None)
            {
                throw new InvalidOperationException(
                    $"Runtime ACL verification failed for '{path}': " +
                    $"rights={entry.FileSystemRights}, inheritance={entry.InheritanceFlags}, " +
                    $"propagation={entry.PropagationFlags}, inherited={entry.IsInherited}, " +
                    $"type={entry.AccessControlType}.");
            }
        }

        if (!actualTrustees.SetEquals(expectedTrustees))
        {
            throw new InvalidOperationException(
                $"Runtime ACL verification failed for '{path}'.");
        }
    }

    private static RuntimeAclSnapshot ReadAclSnapshot(string path)
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

    private static string NormalizeTrustee(string trustee)
    {
        if (trustee.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityIdentifier(trustee).Value;
        }

        return ((SecurityIdentifier)new NTAccount(trustee)
            .Translate(typeof(SecurityIdentifier))).Value;
    }

    private static string GetCurrentUser() =>
        $"{Environment.UserDomainName}\\{Environment.UserName}";
}

public sealed record RuntimeAclSnapshot(
    bool IsDirectory,
    bool AreAccessRulesProtected,
    IReadOnlyList<RuntimeAclEntry> Entries);

public sealed record RuntimeAclEntry(
    string SecurityIdentifier,
    FileSystemRights FileSystemRights,
    AccessControlType AccessControlType,
    InheritanceFlags InheritanceFlags,
    PropagationFlags PropagationFlags,
    bool IsInherited);
