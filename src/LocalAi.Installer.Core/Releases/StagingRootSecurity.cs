using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LocalAi.Installer.Core.Releases;

internal interface IStagingRootFactory
{
    IStagingRootLease CreateExclusive(string requestedPath);
}

internal interface IStagingCreationObserver
{
    void OnStage(StagingCreationStage stage, string path);
}

internal enum StagingCreationStage
{
    HandleOpen,
    LeaseConstruction,
    NonceGeneration,
    MarkerOpen,
    PartialMarkerWrite,
    MarkerFlush,
    PostMarker,
}

internal interface IStagingRootLease : IDisposable
{
    string CanonicalPath { get; }

    void Revalidate();

    void ValidateCreatedFile(SafeFileHandle fileHandle, string expectedPath);

    void Cleanup();
}

/// <summary>
/// Creates a private staging directory with an exclusive native create and holds
/// a no-delete-share handle for the full verification lease. This blocks other
/// users and accidental replacement. A malicious process running as the same
/// user can still open or modify children because the installer must grant that
/// user write access; callers must not treat this as a sandbox from same-user code.
/// </summary>
internal sealed class WindowsStagingRootFactory : IStagingRootFactory
{
    private readonly IStagingCreationObserver? creationObserver;

    public WindowsStagingRootFactory()
    {
    }

    internal WindowsStagingRootFactory(IStagingCreationObserver creationObserver)
    {
        this.creationObserver = creationObserver ??
            throw new ArgumentNullException(nameof(creationObserver));
    }

    public IStagingRootLease CreateExclusive(string requestedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Secure staging is available only on Windows.");
        }

        return CreateExclusiveWindows(requestedPath);
    }

    [SupportedOSPlatform("windows")]
    private IStagingRootLease CreateExclusiveWindows(string requestedPath)
    {
        var canonicalPath = ValidateRequestedPath(requestedPath);
        ValidateAncestors(canonicalPath);
        var security = BuildSecurity();
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        var createdExclusively = false;
        string? ownershipMarker = null;
        var markerCreated = false;
        SafeFileHandle? markerHandle = null;
        SafeFileHandle? rootHandle = null;
        WindowsStagingRootLease? lease = null;
        try
        {
            try
            {
                Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = descriptorPointer,
                    InheritHandle = false,
                };
                if (!CreateDirectoryW(canonicalPath, ref attributes))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw error == 183
                        ? Failure()
                        : new Win32Exception(error);
                }

                createdExclusively = true;
            }
            finally
            {
                Marshal.FreeHGlobal(descriptorPointer);
            }

            creationObserver?.OnStage(
                StagingCreationStage.HandleOpen,
                canonicalPath);
            rootHandle = OpenRootHandle(canonicalPath);

            creationObserver?.OnStage(
                StagingCreationStage.LeaseConstruction,
                canonicalPath);
            lease = new WindowsStagingRootLease(
                canonicalPath,
                rootHandle,
                security);
            rootHandle = null;
            lease.Revalidate();

            creationObserver?.OnStage(
                StagingCreationStage.NonceGeneration,
                canonicalPath);
            var ownershipNonce = RandomNumberGenerator.GetBytes(32);
            ownershipMarker = Path.Combine(
                canonicalPath,
                ".localai-staging-owner-" + Guid.NewGuid().ToString("N"));

            creationObserver?.OnStage(
                StagingCreationStage.MarkerOpen,
                canonicalPath);
            markerHandle = File.OpenHandle(
                ownershipMarker,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                FileOptions.WriteThrough);
            markerCreated = true;
            var markerStream = new FileStream(
                markerHandle,
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: false);
            markerHandle = null;
            using (markerStream)
            {
                markerStream.Write(ownershipNonce.AsSpan(0, 1));
                creationObserver?.OnStage(
                    StagingCreationStage.PartialMarkerWrite,
                    canonicalPath);
                markerStream.Write(ownershipNonce.AsSpan(1));
                creationObserver?.OnStage(
                    StagingCreationStage.MarkerFlush,
                    canonicalPath);
                markerStream.Flush(flushToDisk: true);
            }

            creationObserver?.OnStage(
                StagingCreationStage.PostMarker,
                canonicalPath);
            File.Delete(ownershipMarker);
            markerCreated = false;
            lease.Revalidate();
            return lease;
        }
        catch
        {
            markerHandle?.Dispose();
            if (createdExclusively)
            {
                TryCleanupCreationFailure(
                    canonicalPath,
                    security,
                    ownershipMarker,
                    markerCreated,
                    ref rootHandle,
                    ref lease);
            }

            rootHandle?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ValidateRequestedPath(string requestedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        if (!Path.IsPathFullyQualified(requestedPath) ||
            requestedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            requestedPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            requestedPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw Failure();
        }

        var canonical = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(requestedPath));
        if (!string.Equals(
                canonical,
                Path.TrimEndingDirectorySeparator(requestedPath),
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(Path.GetFileName(canonical)) ||
            !Directory.Exists(Path.GetDirectoryName(canonical)))
        {
            throw Failure();
        }

        return canonical;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
             current is not null;
             current = current.Parent)
        {
            if (!current.Exists ||
                current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity BuildSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw Failure();
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, user);
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void AddFullControl(
        DirectorySecurity security,
        SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenRootHandle(string path)
    {
        var handle = CreateFileW(
            path,
            0x00010000 | 0x00020000 | 0x00100000,
            FileShare.ReadWrite,
            IntPtr.Zero,
            3,
            0x02200000,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static void TryCleanupCreationFailure(
        string path,
        DirectorySecurity expectedSecurity,
        string? markerPath,
        bool markerCreated,
        ref SafeFileHandle? rootHandle,
        ref WindowsStagingRootLease? lease)
    {
        try
        {
            if (lease is null)
            {
                rootHandle ??= OpenRootHandle(path);
                lease = new WindowsStagingRootLease(
                    path,
                    rootHandle,
                    expectedSecurity);
                rootHandle = null;
            }

            _ = lease.TryCleanupCreationFailure(markerPath, markerCreated);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            ReleaseVerificationException or Win32Exception or
            System.Security.SecurityException or ArgumentException)
        {
        }
    }

    private static ReleaseVerificationException Failure() =>
        new("Secure staging root creation failed.");

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(
        string path,
        ref SecurityAttributes securityAttributes);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsStagingRootLease : IStagingRootLease
{
    private readonly SafeFileHandle rootHandle;
    private readonly FileIdentity identity;
    private readonly string physicalPath;
    private readonly HashSet<string> approvedSids;
    private readonly string expectedOwnerSid;
    private bool disposed;

    public WindowsStagingRootLease(
        string canonicalPath,
        SafeFileHandle rootHandle,
        DirectorySecurity expectedSecurity)
    {
        CanonicalPath = canonicalPath;
        this.rootHandle = rootHandle;
        identity = GetIdentity(rootHandle);
        physicalPath = GetFinalPath(rootHandle);
        if (!string.Equals(
                physicalPath,
                CanonicalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure();
        }

        approvedSids = expectedSecurity.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference.Value)
            .ToHashSet(StringComparer.Ordinal);
        expectedOwnerSid = (expectedSecurity.GetOwner(
            typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ??
            throw Failure();
    }

    public string CanonicalPath { get; }

    public void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var currentIdentity = GetIdentity(rootHandle);
        if (currentIdentity != identity ||
            currentIdentity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !string.Equals(
                GetFinalPath(rootHandle),
                physicalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure();
        }

        ValidateAncestors();
        ValidateAcl();
    }

    public void ValidateCreatedFile(
        SafeFileHandle fileHandle,
        string expectedPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(fileHandle);
        var fileIdentity = GetIdentity(fileHandle);
        var actualPath = GetFinalPath(fileHandle);
        var canonicalExpected = Path.GetFullPath(expectedPath);
        var prefix = Path.TrimEndingDirectorySeparator(physicalPath) +
            Path.DirectorySeparatorChar;
        if (fileIdentity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            fileIdentity.VolumeSerialNumber != identity.VolumeSerialNumber ||
            !string.Equals(actualPath, canonicalExpected, StringComparison.OrdinalIgnoreCase) ||
            !actualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure();
        }

        Revalidate();
    }

    public void Cleanup()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Revalidate();
        foreach (var entry in Directory.EnumerateFileSystemEntries(CanonicalPath))
        {
            if (Directory.Exists(entry) ||
                File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure();
            }

            File.Delete(entry);
        }

        Revalidate();
        DeleteEmptyRootByHandle();
    }

    internal bool TryCleanupCreationFailure(
        string? markerPath,
        bool markerCreated)
    {
        Revalidate();
        var entries = Directory.EnumerateFileSystemEntries(CanonicalPath).ToArray();
        if (!markerCreated)
        {
            if (entries.Length != 0)
            {
                return false;
            }
        }
        else
        {
            if (markerPath is null ||
                entries.Length != 1 ||
                !string.Equals(
                    entries[0],
                    markerPath,
                    StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(markerPath) ||
                File.GetAttributes(markerPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            File.Delete(markerPath);
        }

        Revalidate();
        DeleteEmptyRootByHandle();
        return true;
    }

    private void DeleteEmptyRootByHandle()
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                rootHandle,
                4,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        disposed = true;
        rootHandle.Dispose();
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            rootHandle.Dispose();
        }
    }

    private void ValidateAncestors()
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(CanonicalPath)!);
             current is not null;
             current = current.Parent)
        {
            if (!current.Exists ||
                current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure();
            }
        }
    }

    private void ValidateAcl()
    {
        var security = new DirectoryInfo(CanonicalPath).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !string.Equals(owner.Value, expectedOwnerSid, StringComparison.Ordinal))
        {
            throw Failure();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !approvedSids.Contains(sid.Value) ||
                (rule.FileSystemRights & FileSystemRights.FullControl) !=
                    FileSystemRights.FullControl)
            {
                throw Failure();
            }

            seen.Add(sid.Value);
        }

        if (!seen.SetEquals(approvedSids))
        {
            throw Failure();
        }
    }

    private static FileIdentity GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow,
            (FileAttributes)information.FileAttributes);
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (required == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var buffer = new System.Text.StringBuilder(checked((int)required + 1));
        var written = GetFinalPathNameByHandleW(
            handle,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (written == 0 || written >= buffer.Capacity)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var path = buffer.ToString();
        const string unc = @"\\?\UNC\";
        const string extended = @"\\?\";
        if (path.StartsWith(unc, StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[unc.Length..];
        }
        else if (path.StartsWith(extended, StringComparison.OrdinalIgnoreCase))
        {
            path = path[extended.Length..];
        }

        return Path.GetFullPath(path);
    }

    private static ReleaseVerificationException Failure() =>
        new("Staging root identity verification failed.");

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow,
        FileAttributes Attributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        System.Text.StringBuilder? filePath,
        uint filePathLength,
        uint flags);
}
