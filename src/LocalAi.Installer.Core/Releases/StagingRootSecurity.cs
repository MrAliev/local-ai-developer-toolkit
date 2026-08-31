using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Releases;

internal interface IStagingRootFactory
{
    IStagingRootLease CreateExclusive(string requestedPath);
}

internal interface IAtomicDirectoryCreator
{
    SafeFileHandle CreateDirectory(
        SafeFileHandle parentHandle,
        string leafName,
        ReadOnlySpan<byte> securityDescriptor);
}

internal interface IStagingCreationObserver
{
    void OnStage(StagingCreationStage stage, string path);
}

internal enum StagingCreationStage
{
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

    IRetainedStagingFile RetainFile(string relativePath);

    void ValidateExactLayout(IEnumerable<string> approvedRelativePaths);

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
    private readonly IAtomicDirectoryCreator atomicDirectoryCreator;
    private readonly IStagingCreationObserver? creationObserver;

    public WindowsStagingRootFactory()
        : this(new NativeNtAtomicDirectoryCreator(), null)
    {
    }

    internal WindowsStagingRootFactory(
        IAtomicDirectoryCreator atomicDirectoryCreator)
        : this(atomicDirectoryCreator, null)
    {
    }

    internal WindowsStagingRootFactory(
        IAtomicDirectoryCreator atomicDirectoryCreator,
        IStagingCreationObserver? creationObserver)
    {
        this.atomicDirectoryCreator = atomicDirectoryCreator ??
            throw new ArgumentNullException(nameof(atomicDirectoryCreator));
        this.creationObserver = creationObserver;
    }

    public IStagingRootLease CreateExclusive(string requestedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Secure staging is available only on Windows.");
        }

        try
        {
            return CreateExclusiveWindows(requestedPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReleaseVerificationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            Win32Exception or System.Security.SecurityException or
            ArgumentException)
        {
            throw Failure();
        }
    }

    [SupportedOSPlatform("windows")]
    private IStagingRootLease CreateExclusiveWindows(string requestedPath)
    {
        var canonicalPath = ValidateRequestedPath(requestedPath);
        ValidateAncestors(canonicalPath);
        var canonicalParent = Path.GetDirectoryName(canonicalPath)!;
        var leafName = Path.GetFileName(canonicalPath);
        var security = BuildSecurity();
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        string? ownershipMarker = null;
        var markerCreated = false;
        SafeFileHandle? markerHandle = null;
        SafeFileHandle? parentHandle = null;
        SafeFileHandle? rootHandle = null;
        WindowsStagingRootLease? lease = null;
        var rootHandleBoundToRequest = false;
        try
        {
            parentHandle = OpenParentHandle(canonicalParent);
            WindowsStagingRootLease.ValidateDirectoryHandle(
                parentHandle,
                canonicalParent);
            rootHandle = atomicDirectoryCreator.CreateDirectory(
                parentHandle,
                leafName,
                descriptor);
            WindowsStagingRootLease.ValidateDirectoryHandle(
                rootHandle,
                canonicalPath);
            rootHandleBoundToRequest = true;

            creationObserver?.OnStage(
                StagingCreationStage.LeaseConstruction,
                canonicalPath);
            lease = new WindowsStagingRootLease(
                canonicalPath,
                canonicalParent,
                parentHandle,
                rootHandle,
                security);
            parentHandle = null;
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
            if (rootHandleBoundToRequest)
            {
                TryCleanupCreationFailure(
                    canonicalPath,
                    canonicalParent,
                    security,
                    ownershipMarker,
                    markerCreated,
                    ref parentHandle,
                    ref rootHandle,
                    ref lease);
            }

            rootHandle?.Dispose();
            parentHandle?.Dispose();
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
    private static SafeFileHandle OpenParentHandle(string path)
    {
        var handle = CreateFileW(
            path,
            0x00000001 | 0x00000020 | 0x00000080 | 0x00020000 | 0x00100000,
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
        string parentPath,
        DirectorySecurity expectedSecurity,
        string? markerPath,
        bool markerCreated,
        ref SafeFileHandle? parentHandle,
        ref SafeFileHandle? rootHandle,
        ref WindowsStagingRootLease? lease)
    {
        try
        {
            if (lease is null)
            {
                if (parentHandle is null || rootHandle is null)
                {
                    return;
                }

                lease = new WindowsStagingRootLease(
                    path,
                    parentPath,
                    parentHandle,
                    rootHandle,
                    expectedSecurity);
                parentHandle = null;
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

internal sealed class NativeNtAtomicDirectoryCreator : IAtomicDirectoryCreator
{
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Delete = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonalert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;

    public SafeFileHandle CreateDirectory(
        SafeFileHandle parentHandle,
        string leafName,
        ReadOnlySpan<byte> securityDescriptor)
    {
        ArgumentNullException.ThrowIfNull(parentHandle);
        if (parentHandle.IsInvalid || parentHandle.IsClosed ||
            string.IsNullOrWhiteSpace(leafName) ||
            leafName is "." or ".." ||
            leafName.IndexOfAny(['\\', '/', ':']) >= 0 ||
            securityDescriptor.IsEmpty)
        {
            throw Failure();
        }

        var nameBuffer = IntPtr.Zero;
        var namePointer = IntPtr.Zero;
        var descriptorPointer = IntPtr.Zero;
        var parentAddedRef = false;
        IntPtr nativeHandle = IntPtr.Zero;
        try
        {
            nameBuffer = Marshal.StringToHGlobalUni(leafName);
            var name = new UnicodeString
            {
                Length = checked((ushort)(leafName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((leafName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer,
            };
            namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(name, namePointer, fDeleteOld: false);

            var descriptor = securityDescriptor.ToArray();
            descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);

            parentHandle.DangerousAddRef(ref parentAddedRef);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = namePointer,
                Attributes = ObjCaseInsensitive,
                SecurityDescriptor = descriptorPointer,
            };
            var status = NtCreateFile(
                out nativeHandle,
                FileListDirectory | FileReadAttributes | Delete | ReadControl | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                FileShareRead | FileShareWrite,
                FileCreate,
                FileDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0 || nativeHandle == IntPtr.Zero || nativeHandle == new IntPtr(-1))
            {
                if (nativeHandle != IntPtr.Zero && nativeHandle != new IntPtr(-1))
                {
                    new SafeFileHandle(nativeHandle, ownsHandle: true).Dispose();
                    nativeHandle = IntPtr.Zero;
                }

                throw Failure();
            }

            var result = new SafeFileHandle(nativeHandle, ownsHandle: true);
            nativeHandle = IntPtr.Zero;
            return result;
        }
        catch (Exception exception) when (
            exception is OverflowException or OutOfMemoryException or
            ArgumentException)
        {
            throw Failure();
        }
        finally
        {
            if (nativeHandle != IntPtr.Zero && nativeHandle != new IntPtr(-1))
            {
                new SafeFileHandle(nativeHandle, ownsHandle: true).Dispose();
            }

            if (parentAddedRef)
            {
                parentHandle.DangerousRelease();
            }

            if (descriptorPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(descriptorPointer);
            }

            if (namePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(namePointer);
            }

            if (nameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(nameBuffer);
            }
        }
    }

    private static ReleaseVerificationException Failure() =>
        new("Secure staging root creation failed.");

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsStagingRootLease : IStagingRootLease
{
    private readonly SafeFileHandle parentHandle;
    private readonly SafeFileHandle rootHandle;
    private readonly FileIdentity parentIdentity;
    private readonly string parentPhysicalPath;
    private readonly FileIdentity identity;
    private readonly string physicalPath;
    private readonly HashSet<string> approvedSids;
    private readonly string expectedOwnerSid;
    private bool disposed;

    public WindowsStagingRootLease(
        string canonicalPath,
        string canonicalParentPath,
        SafeFileHandle parentHandle,
        SafeFileHandle rootHandle,
        DirectorySecurity expectedSecurity)
    {
        CanonicalPath = canonicalPath;
        this.parentHandle = parentHandle;
        this.rootHandle = rootHandle;
        parentIdentity = GetIdentity(parentHandle);
        parentPhysicalPath = GetFinalPath(parentHandle);
        if (!string.Equals(
                parentPhysicalPath,
                canonicalParentPath,
                StringComparison.OrdinalIgnoreCase) ||
            parentIdentity.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Failure();
        }

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
        var currentParentIdentity = GetIdentity(parentHandle);
        if (currentParentIdentity != parentIdentity ||
            currentParentIdentity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !string.Equals(
                GetFinalPath(parentHandle),
                parentPhysicalPath,
                StringComparison.OrdinalIgnoreCase) ||
            currentIdentity != identity ||
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

    public IRetainedStagingFile RetainFile(string relativePath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !string.Equals(
                Path.GetFileName(relativePath),
                relativePath,
                StringComparison.Ordinal) ||
            relativePath.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            throw Failure();
        }

        Revalidate();
        var expectedPath = Path.Combine(CanonicalPath, relativePath);
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                expectedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.RandomAccess);
            ValidateCreatedFile(handle, expectedPath);
            var retained = new WindowsRetainedStagingFile(
                relativePath,
                expectedPath,
                handle);
            handle = null;
            return retained;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public void ValidateExactLayout(IEnumerable<string> approvedRelativePaths)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(approvedRelativePaths);
        Revalidate();
        if (Directory.EnumerateDirectories(CanonicalPath).Any())
        {
            throw Failure();
        }

        var expected = approvedRelativePaths.ToHashSet(StringComparer.Ordinal);
        var actual = Directory.EnumerateFiles(CanonicalPath)
            .Select(path => Path.GetFileName(path)!)
            .ToArray();
        if (!expected.SetEquals(actual))
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
        parentHandle.Dispose();
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            rootHandle.Dispose();
            parentHandle.Dispose();
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
                    FileSystemRights.FullControl ||
                rule.InheritanceFlags !=
                    (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) ||
                rule.PropagationFlags != PropagationFlags.None)
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

    internal static void ValidateDirectoryHandle(
        SafeFileHandle handle,
        string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw Failure();
        }

        var identity = GetIdentity(handle);
        if (identity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !identity.Attributes.HasFlag(FileAttributes.Directory) ||
            !string.Equals(
                GetFinalPath(handle),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure();
        }
    }

    internal static FileIdentity GetIdentity(SafeFileHandle handle)
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

    internal static string GetFinalPath(SafeFileHandle handle)
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

    internal readonly record struct FileIdentity(
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
