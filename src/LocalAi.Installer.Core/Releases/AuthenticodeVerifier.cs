using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalAi.Installer.Core.Releases;

public interface IAuthenticodeVerifier
{
    bool IsTrusted(string path, string approvedPublisher);
}

public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    public bool IsTrusted(string path, string approvedPublisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPublisher);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return IsTrustedWindows(path, approvedPublisher);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrustedWindows(string path, string approvedPublisher)
    {
        var fileInfo = new WinTrustFileInfo(Path.GetFullPath(path));
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);
            var trustData = WinTrustData.ForFile(filePointer);
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            try
            {
                if (result != 0)
                {
                    return false;
                }

#pragma warning disable SYSLIB0057
                using var certificate = new X509Certificate2(
                    X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                return string.Equals(
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    approvedPublisher,
                    StringComparison.Ordinal);
            }
            catch (Exception exception) when (
                exception is CryptographicException or IOException or
                UnauthorizedAccessException or ArgumentException or Win32Exception)
            {
                return false;
            }
            finally
            {
                trustData.StateAction = WinTrustStateAction.Close;
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static readonly Guid GenericVerifyV2 =
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
}
