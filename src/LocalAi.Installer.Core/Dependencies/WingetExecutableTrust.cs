using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    Changed,
    UnsupportedPlatform,
}

public sealed record TrustedExecutable(
    string CanonicalPath,
    string Identity,
    string Publisher);

public sealed record ExecutableTrustResult(
    ExecutableTrustStatus Status,
    TrustedExecutable? Executable);

public interface IWinGetExecutableTrust
{
    ExecutableTrustResult Resolve(string snapshotPath);
    ExecutableTrustResult Revalidate(TrustedExecutable executable);
}

/// <summary>
/// Resolves WinGet to a physical, Microsoft-signed executable and binds its
/// canonical path and content identity. IProcessRunner accepts a path rather
/// than an already-open executable handle, so a residual replacement race
/// remains between final revalidation and process creation. Revalidation is
/// deliberately performed immediately before RunAsync to minimize that window.
/// </summary>
public sealed class WindowsWingetExecutableTrust : IWinGetExecutableTrust
{
    private const string ExpectedPublisher = "Microsoft Corporation";
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";
    private const string PackageSuffix = "_x64__8wekyb3d8bbwe";
    private const string PackagesRegistryPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public ExecutableTrustResult Resolve(string snapshotPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Failure(ExecutableTrustStatus.UnsupportedPlatform);
        }

        if (string.IsNullOrWhiteSpace(snapshotPath) ||
            !Path.IsPathFullyQualified(snapshotPath))
        {
            return Failure(ExecutableTrustStatus.InvalidPath);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(snapshotPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            return Failure(ExecutableTrustStatus.InvalidPath);
        }

        if (!string.Equals(
                Path.GetFileName(fullPath),
                "winget.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ExecutableTrustStatus.InvalidPath);
        }

        string? candidate;
        try
        {
            candidate = IsAppExecutionAlias(fullPath)
                ? ResolveDesktopAppInstallerWinget()
                : fullPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException)
        {
            return Failure(ExecutableTrustStatus.Unavailable);
        }

        if (candidate is null)
        {
            return Failure(ExecutableTrustStatus.Unavailable);
        }

        return ResolvePhysicalAndVerify(candidate);
    }

    public ExecutableTrustResult Revalidate(TrustedExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        var current = ResolvePhysicalAndVerify(executable.CanonicalPath);
        return current.Status == ExecutableTrustStatus.Trusted &&
               current.Executable == executable
            ? current
            : Failure(ExecutableTrustStatus.Changed);
    }

    private static ExecutableTrustResult ResolvePhysicalAndVerify(
        string candidate)
    {
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!Path.IsPathFullyQualified(fullPath) ||
                !string.Equals(
                    Path.GetFileName(fullPath),
                    "winget.exe",
                    StringComparison.OrdinalIgnoreCase) ||
                HasReparsePoint(fullPath))
            {
                return Failure(ExecutableTrustStatus.InvalidPath);
            }

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
            if (!VerifyAuthenticode(canonicalPath, out var publisher, out var thumbprint) ||
                !string.Equals(
                    publisher,
                    ExpectedPublisher,
                    StringComparison.Ordinal))
            {
                return Failure(ExecutableTrustStatus.UntrustedPublisher);
            }

            return new ExecutableTrustResult(
                ExecutableTrustStatus.Trusted,
                new TrustedExecutable(
                    canonicalPath,
                    $"SHA256:{hash};SIGNER:{thumbprint}",
                    publisher));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            Win32Exception or CryptographicException or
            ArgumentException or NotSupportedException)
        {
            return Failure(ExecutableTrustStatus.Unavailable);
        }
    }

    private static bool IsAppExecutionAlias(string path)
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return false;
        }

        var expected = Path.Combine(
            localAppData,
            "Microsoft",
            "WindowsApps",
            "winget.exe");
        if (!string.Equals(
                path,
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ResolveDesktopAppInstallerWinget()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(
            PackagesRegistryPath,
            writable: false);
        if (packages is null)
        {
            return null;
        }

        var candidates = new List<(Version Version, string Path)>();
        foreach (var packageName in packages.GetSubKeyNames())
        {
            if (!packageName.StartsWith(PackagePrefix, StringComparison.Ordinal) ||
                !packageName.EndsWith(PackageSuffix, StringComparison.Ordinal) ||
                !Version.TryParse(
                    packageName[
                        PackagePrefix.Length..
                        ^PackageSuffix.Length],
                    out var version))
            {
                continue;
            }

            using var package = packages.OpenSubKey(
                packageName,
                writable: false);
            if (package?.GetValue("PackageRootFolder") is not string root ||
                string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var fullRoot = Path.GetFullPath(root);
            if (!string.Equals(
                    Path.GetFileName(fullRoot),
                    packageName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var winget = Path.GetFullPath(
                Path.Combine(fullRoot, "winget.exe"));
            if (IsContainedBy(fullRoot, winget) && File.Exists(winget))
            {
                candidates.Add((version, winget));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
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

    private static bool HasReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
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

    private static string ResolvePhysicalPath(SafeFileHandle handle)
    {
        var requiredLength = GetFinalPathNameByHandleW(
            handle,
            null,
            0,
            0);
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
            var status = WinVerifyTrust(
                new IntPtr(-1),
                ref action,
                ref trustData);
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
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag =
                    X509RevocationFlag.EntireChain;
                chain.ChainPolicy.VerificationFlags =
                    X509VerificationFlags.NoFlag;
                if (!chain.Build(certificate))
                {
                    return false;
                }

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

        return path.StartsWith(
            extendedPrefix,
            StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static ExecutableTrustResult Failure(
        ExecutableTrustStatus status) =>
        new(status, null);

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

    [DllImport(
        "wintrust.dll",
        ExactSpelling = true,
        PreserveSig = true,
        SetLastError = false)]
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
}
