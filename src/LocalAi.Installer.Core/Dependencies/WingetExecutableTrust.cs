using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LocalAi.Installer.Core.Dependencies;

public enum ExecutableTrustStatus
{
    Trusted,
    InvalidPath,
    Unavailable,
    UntrustedPublisher,
    UntrustedAcl,
    Changed,
    UnsupportedPlatform,
}

public sealed record TrustedExecutable(
    string CanonicalPath,
    string Identity,
    string Publisher,
    string PackageFullName = "",
    string PackageRoot = "");

public sealed record ExecutableTrustResult(
    ExecutableTrustStatus Status,
    TrustedExecutable? Executable);

public interface IWinGetExecutableTrust
{
    ExecutableTrustResult Resolve(string snapshotPath);
    ExecutableTrustResult Revalidate(TrustedExecutable executable);
}

internal sealed record RegisteredWingetPackage(
    string PackageFullName,
    string PackageFamilyName,
    string PackageRoot);

internal sealed record WingetExecutableInspection(
    ExecutableTrustStatus Status,
    string? CanonicalPath,
    string? Identity,
    string? Publisher);

internal interface IWindowsWingetTrustPlatform
{
    string ProgramFilesPath { get; }
    string LocalAppDataPath { get; }
    IReadOnlyList<RegisteredWingetPackage> GetRegisteredPackages();
    bool IsProtectedPath(string path);
    WingetExecutableInspection InspectExecutable(string path);
}

/// <summary>
/// Binds WinGet to the registered Microsoft Desktop App Installer package under
/// the protected Program Files WindowsApps root. The package load directory,
/// executable ACL, canonical path, Authenticode publisher, and content hash are
/// checked twice. IProcessRunner accepts a path rather than an already-open
/// executable handle, so a residual replacement race remains between final
/// revalidation and process creation; immediate revalidation minimizes it.
/// </summary>
public sealed class WindowsWingetExecutableTrust : IWinGetExecutableTrust
{
    private const string ExpectedPublisher = "Microsoft Corporation";
    private const string ExpectedPackageFamily =
        "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";
    private const string PackageSuffix = "_x64__8wekyb3d8bbwe";

    private readonly IWindowsWingetTrustPlatform _platform;

    public WindowsWingetExecutableTrust()
        : this(new WindowsWingetTrustPlatform())
    {
    }

    internal WindowsWingetExecutableTrust(
        IWindowsWingetTrustPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public ExecutableTrustResult Resolve(string snapshotPath)
    {
        if (_platform is WindowsWingetTrustPlatform &&
            !OperatingSystem.IsWindows())
        {
            return Failure(ExecutableTrustStatus.UnsupportedPlatform);
        }

        if (string.IsNullOrWhiteSpace(snapshotPath) ||
            !Path.IsPathFullyQualified(snapshotPath))
        {
            return Failure(ExecutableTrustStatus.InvalidPath);
        }

        string fullSnapshot;
        string windowsAppsRoot;
        string alias;
        try
        {
            fullSnapshot = Path.GetFullPath(snapshotPath);
            windowsAppsRoot = Path.GetFullPath(
                Path.Combine(_platform.ProgramFilesPath, "WindowsApps"));
            alias = Path.GetFullPath(
                Path.Combine(
                    _platform.LocalAppDataPath,
                    "Microsoft",
                    "WindowsApps",
                    "winget.exe"));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            return Failure(ExecutableTrustStatus.InvalidPath);
        }

        var packages = _platform.GetRegisteredPackages();
        var registered = packages
            .Select(package => ValidatePackage(package, windowsAppsRoot))
            .Where(package => package is not null)
            .Cast<ValidatedPackage>()
            .OrderByDescending(package => package.Version)
            .FirstOrDefault();
        if (registered is null)
        {
            return Failure(ExecutableTrustStatus.InvalidPath);
        }

        if (!string.Equals(fullSnapshot, alias, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                fullSnapshot,
                registered.ExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ExecutableTrustStatus.InvalidPath);
        }

        if (!_platform.IsProtectedPath(windowsAppsRoot) ||
            !_platform.IsProtectedPath(registered.Package.PackageRoot) ||
            !_platform.IsProtectedPath(registered.ExecutablePath))
        {
            return Failure(ExecutableTrustStatus.UntrustedAcl);
        }

        var inspection = _platform.InspectExecutable(
            registered.ExecutablePath);
        if (inspection.Status != ExecutableTrustStatus.Trusted ||
            string.IsNullOrWhiteSpace(inspection.CanonicalPath) ||
            string.IsNullOrWhiteSpace(inspection.Identity) ||
            !string.Equals(
                inspection.Publisher,
                ExpectedPublisher,
                StringComparison.Ordinal) ||
            !string.Equals(
                inspection.CanonicalPath,
                registered.ExecutablePath,
                StringComparison.OrdinalIgnoreCase) ||
            !IsContainedBy(
                registered.Package.PackageRoot,
                inspection.CanonicalPath))
        {
            return Failure(
                inspection.Status == ExecutableTrustStatus.UntrustedPublisher
                    ? ExecutableTrustStatus.UntrustedPublisher
                    : ExecutableTrustStatus.InvalidPath);
        }

        return new ExecutableTrustResult(
            ExecutableTrustStatus.Trusted,
            new TrustedExecutable(
                inspection.CanonicalPath,
                inspection.Identity,
                inspection.Publisher!,
                registered.Package.PackageFullName,
                registered.Package.PackageRoot));
    }

    public ExecutableTrustResult Revalidate(TrustedExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        var current = Resolve(executable.CanonicalPath);
        return current.Status == ExecutableTrustStatus.Trusted &&
               current.Executable == executable
            ? current
            : Failure(ExecutableTrustStatus.Changed);
    }

    private static ValidatedPackage? ValidatePackage(
        RegisteredWingetPackage package,
        string windowsAppsRoot)
    {
        if (!string.Equals(
                package.PackageFamilyName,
                ExpectedPackageFamily,
                StringComparison.Ordinal) ||
            !package.PackageFullName.StartsWith(
                PackagePrefix,
                StringComparison.Ordinal) ||
            !package.PackageFullName.EndsWith(
                PackageSuffix,
                StringComparison.Ordinal))
        {
            return null;
        }

        var versionText = package.PackageFullName[
            PackagePrefix.Length..
            ^PackageSuffix.Length];
        if (versionText.Split('.').Length != 4 ||
            !versionText.Split('.').All(
                component => component.Length > 0 &&
                             component.All(char.IsAsciiDigit)) ||
            !Version.TryParse(versionText, out var version))
        {
            return null;
        }

        string expectedRoot;
        string packageRoot;
        string executable;
        try
        {
            expectedRoot = Path.GetFullPath(
                Path.Combine(
                    windowsAppsRoot,
                    package.PackageFullName));
            packageRoot = Path.GetFullPath(package.PackageRoot);
            executable = Path.GetFullPath(
                Path.Combine(packageRoot, "winget.exe"));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            return null;
        }

        if (!string.Equals(
                expectedRoot,
                packageRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !IsContainedBy(windowsAppsRoot, packageRoot) ||
            !IsContainedBy(packageRoot, executable))
        {
            return null;
        }

        return new ValidatedPackage(package, version, executable);
    }

    private static bool IsContainedBy(string root, string path)
    {
        var rootWithSeparator =
            root.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(
            rootWithSeparator,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ExecutableTrustResult Failure(
        ExecutableTrustStatus status) =>
        new(status, null);

    private sealed record ValidatedPackage(
        RegisteredWingetPackage Package,
        Version Version,
        string ExecutablePath);
}

internal sealed class WindowsWingetTrustPlatform : IWindowsWingetTrustPlatform
{
    private const string PackagesRegistryPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";
    private const string ExpectedPublisher = "Microsoft Corporation";
    public string ProgramFilesPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine,
                        RegistryView.Registry64)
                    .OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion",
                        writable: false);
                if (key?.GetValue("ProgramFilesDir") is string programFiles &&
                    !string.IsNullOrWhiteSpace(programFiles))
                {
                    return programFiles;
                }
            }

            return Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
        }
    }

    public string LocalAppDataPath =>
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

    [SupportedOSPlatform("windows")]
    public IReadOnlyList<RegisteredWingetPackage> GetRegisteredPackages()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(
            PackagesRegistryPath,
            writable: false);
        if (packages is null)
        {
            return [];
        }

        var results = new List<RegisteredWingetPackage>();
        foreach (var packageFullName in packages.GetSubKeyNames())
        {
            if (!packageFullName.StartsWith(
                    PackagePrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }

            using var package = packages.OpenSubKey(
                packageFullName,
                writable: false);
            if (package?.GetValue("PackageRootFolder") is not string root)
            {
                continue;
            }

            var publisherSeparator = packageFullName.LastIndexOf("__", StringComparison.Ordinal);
            var publisherId = publisherSeparator < 0
                ? string.Empty
                : packageFullName[(publisherSeparator + 2)..];
            results.Add(
                new RegisteredWingetPackage(
                    packageFullName,
                    $"Microsoft.DesktopAppInstaller_{publisherId}",
                    root));
        }

        return results;
    }

    [SupportedOSPlatform("windows")]
    public bool IsProtectedPath(string path)
    {
        try
        {
            const FileSystemRights dangerousRights =
                FileSystemRights.WriteData |
                FileSystemRights.AppendData |
                FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.WriteAttributes |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;
            var fullPath = Path.GetFullPath(path);
            if (HasReparsePoint(fullPath))
            {
                return false;
            }

            FileSystemSecurity security = Directory.Exists(fullPath)
                ? FileSystemAclExtensions.GetAccessControl(
                    new DirectoryInfo(fullPath),
                    AccessControlSections.Access |
                    AccessControlSections.Owner)
                : FileSystemAclExtensions.GetAccessControl(
                    new FileInfo(fullPath),
                    AccessControlSections.Access |
                    AccessControlSections.Owner);
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is null)
            {
                return false;
            }

            var principals = CurrentAndBroadPrincipals(identity);
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && principals.Contains(owner))
            {
                return false;
            }

            var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    rule.IdentityReference is SecurityIdentifier sid &&
                    principals.Contains(sid) &&
                    (rule.FileSystemRights & dangerousRights) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return HasNoDangerousWriteAccess(path);
        }
        catch (Exception exception) when (
            exception is IOException or System.Security.SecurityException or
            ArgumentException or IdentityNotMappedException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    public WingetExecutableInspection InspectExecutable(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            using var handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);
            var canonicalPath = ResolvePhysicalPath(handle);
            if (!string.Equals(
                    canonicalPath,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(ExecutableTrustStatus.InvalidPath);
            }

            using var stream = new FileStream(handle, FileAccess.Read);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!VerifyAuthenticode(
                    canonicalPath,
                    out var publisher,
                    out var thumbprint) ||
                !string.Equals(
                    publisher,
                    ExpectedPublisher,
                    StringComparison.Ordinal))
            {
                return Failure(ExecutableTrustStatus.UntrustedPublisher);
            }

            return new WingetExecutableInspection(
                ExecutableTrustStatus.Trusted,
                canonicalPath,
                $"SHA256:{hash};SIGNER:{thumbprint}",
                publisher);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            Win32Exception or CryptographicException or
            ArgumentException or NotSupportedException)
        {
            return Failure(ExecutableTrustStatus.Unavailable);
        }
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<SecurityIdentifier> CurrentAndBroadPrincipals(
        WindowsIdentity identity)
    {
        var result = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.WorldSid, null),
            new(WellKnownSidType.AuthenticatedUserSid, null),
            new(WellKnownSidType.BuiltinUsersSid, null),
        };
        result.Add(identity.User!);
        var principal = new WindowsPrincipal(identity);
        foreach (var group in identity.Groups ?? [])
        {
            if (group is SecurityIdentifier sid && principal.IsInRole(sid))
            {
                result.Add(sid);
            }
        }

        return result;
    }

    private static bool HasReparsePoint(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        var current = root;
        foreach (var segment in path[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasNoDangerousWriteAccess(string path)
    {
        uint[] dangerousAccessRights =
        [
            0x00000002, // FILE_WRITE_DATA / FILE_ADD_FILE
            0x00000004, // FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY
            0x00000010, // FILE_WRITE_EA
            0x00000040, // FILE_DELETE_CHILD
            0x00000100, // FILE_WRITE_ATTRIBUTES
            0x00010000, // DELETE
            0x00040000, // WRITE_DAC
            0x00080000, // WRITE_OWNER
        ];
        foreach (var accessRight in dangerousAccessRights)
        {
            using var handle = CreateFileW(
                path,
                accessRight,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                3,
                0x02200000,
                IntPtr.Zero);
            if (!handle.IsInvalid || Marshal.GetLastWin32Error() != 5)
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolvePhysicalPath(SafeFileHandle handle)
    {
        var requiredLength = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (requiredLength == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var buffer = new StringBuilder(checked((int)requiredLength + 1));
        var written = GetFinalPathNameByHandleW(
            handle,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (written == 0 || written >= buffer.Capacity)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return Path.GetFullPath(NormalizeExtendedPath(buffer.ToString()));
    }

    private static bool VerifyAuthenticode(
        string path,
        out string publisher,
        out string thumbprint)
    {
        publisher = string.Empty;
        thumbprint = string.Empty;
        var fileInfo = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);
            var trustData = WinTrustData.ForFile(filePointer);
            var action = WinTrustActionGenericVerifyV2;
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            try
            {
                if (status != 0)
                {
                    return false;
                }

#pragma warning disable SYSLIB0057
                using var certificate = new X509Certificate2(
                    X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                publisher = certificate.GetNameInfo(
                    X509NameType.SimpleName,
                    forIssuer: false);
                thumbprint = certificate.Thumbprint;
                return !string.IsNullOrWhiteSpace(thumbprint);
            }
            finally
            {
                trustData.StateAction = WinTrustStateAction.Close;
                _ = WinVerifyTrust(
                    new IntPtr(-1),
                    ref action,
                    ref trustData);
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static string NormalizeExtendedPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static WingetExecutableInspection Failure(
        ExecutableTrustStatus status) =>
        new(status, null, null, null);

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private enum WinTrustStateAction : uint
    {
        Verify = 1,
        Close = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public WinTrustFileInfo(string filePath)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
        }

        public uint StructureSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public WinTrustStateAction StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;

        public static WinTrustData ForFile(IntPtr fileInfo) =>
            new()
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = fileInfo,
                StateAction = WinTrustStateAction.Verify,
                ProviderFlags = 0x00000040,
            };
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder? filePath,
        uint filePathLength,
        uint flags);

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
