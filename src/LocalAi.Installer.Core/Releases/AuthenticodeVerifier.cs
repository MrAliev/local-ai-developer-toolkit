using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalAi.Installer.Core.Releases;

public sealed class AuthenticodePublisherPolicy
{
    private readonly byte[] subjectPublicKeyInfoSha256;

    /// <summary>
    /// Whether this policy names nobody.
    ///
    /// The shipped policy is a placeholder -- CN=LocalAi and a SHA-256 of sixty-four
    /// zeroes -- because there is no code-signing certificate yet. Enforcing it would
    /// not harden an installation: no file can match it, so every installation would be
    /// refused, and refused with a signature mismatch that says nothing about why.
    ///
    /// Callers ask this so they can refuse to enforce, loudly, instead.
    /// </summary>
    public bool IsPlaceholder => subjectPublicKeyInfoSha256.All(part => part == 0);

    public AuthenticodePublisherPolicy(
        string canonicalDistinguishedName,
        string subjectPublicKeyInfoSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDistinguishedName);
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfoSha256);
        string normalizedName;
        try
        {
            normalizedName = new X500DistinguishedName(canonicalDistinguishedName)
                .Decode(CanonicalNameFlags);
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException(
                "The signer distinguished name is invalid.",
                nameof(canonicalDistinguishedName),
                exception);
        }

        if (!string.Equals(
                normalizedName,
                canonicalDistinguishedName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The signer distinguished name must use canonical X500 formatting.",
                nameof(canonicalDistinguishedName));
        }
        if (subjectPublicKeyInfoSha256.Length != 64)
        {
            throw new ArgumentException(
                "The signer SPKI hash must be canonical SHA-256 hexadecimal.",
                nameof(subjectPublicKeyInfoSha256));
        }

        try
        {
            this.subjectPublicKeyInfoSha256 =
                Convert.FromHexString(subjectPublicKeyInfoSha256);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The signer SPKI hash must be canonical SHA-256 hexadecimal.",
                nameof(subjectPublicKeyInfoSha256),
                exception);
        }

        if (!string.Equals(
                Convert.ToHexString(this.subjectPublicKeyInfoSha256),
                subjectPublicKeyInfoSha256,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The signer SPKI hash must be canonical SHA-256 hexadecimal.",
                nameof(subjectPublicKeyInfoSha256));
        }

        CanonicalDistinguishedName = canonicalDistinguishedName;
    }

    public string CanonicalDistinguishedName { get; }

    internal ReadOnlySpan<byte> SubjectPublicKeyInfoSha256 =>
        subjectPublicKeyInfoSha256;

    internal const X500DistinguishedNameFlags CanonicalNameFlags =
        X500DistinguishedNameFlags.Reversed |
        X500DistinguishedNameFlags.UseCommas |
        X500DistinguishedNameFlags.UseUTF8Encoding;
}

public interface IAuthenticodeVerifier
{
    bool IsTrusted(string path, AuthenticodePublisherPolicy policy);
}

internal sealed class AuthenticodeSignerIdentity
{
    public AuthenticodeSignerIdentity(
        string canonicalDistinguishedName,
        ReadOnlySpan<byte> subjectPublicKeyInfoSha256)
    {
        CanonicalDistinguishedName = canonicalDistinguishedName;
        SubjectPublicKeyInfoSha256 = subjectPublicKeyInfoSha256.ToArray();
    }

    public string CanonicalDistinguishedName { get; }

    public byte[] SubjectPublicKeyInfoSha256 { get; }
}

internal interface IWinTrustSignerProvider
{
    IReadOnlyList<AuthenticodeSignerIdentity> GetVerifiedSigners(string path);
}

public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    private readonly IWinTrustSignerProvider provider;

    public WindowsAuthenticodeVerifier()
        : this(new NativeWinTrustSignerProvider())
    {
    }

    internal WindowsAuthenticodeVerifier(IWinTrustSignerProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public bool IsTrusted(string path, AuthenticodePublisherPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(policy);
        try
        {
            var signers = provider.GetVerifiedSigners(Path.GetFullPath(path));
            return signers.Count > 0 && signers.All(signer =>
                string.Equals(
                    signer.CanonicalDistinguishedName,
                    policy.CanonicalDistinguishedName,
                    StringComparison.Ordinal) &&
                CryptographicOperations.FixedTimeEquals(
                    signer.SubjectPublicKeyInfoSha256,
                    policy.SubjectPublicKeyInfoSha256));
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or
            UnauthorizedAccessException or ArgumentException or
            Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}

internal sealed class NativeWinTrustSignerProvider : IWinTrustSignerProvider
{
    public IReadOnlyList<AuthenticodeSignerIdentity> GetVerifiedSigners(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return GetVerifiedSignersWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AuthenticodeSignerIdentity> GetVerifiedSignersWindows(
        string path)
    {
        var secondaryCount = Verify(path, signatureIndex: 0, discoverCount: true).SecondaryCount;
        if (secondaryCount > 15)
        {
            throw new CryptographicException("Too many Authenticode signatures.");
        }

        var result = new List<AuthenticodeSignerIdentity>(checked((int)secondaryCount + 1));
        for (uint index = 0; index <= secondaryCount; index++)
        {
            var verification = Verify(path, index, discoverCount: false);
            if (verification.Signers.Count != 1)
            {
                throw new CryptographicException("Unexpected Authenticode signer state.");
            }

            result.Add(verification.Signers[0]);
        }

        return result.AsReadOnly();
    }

    [SupportedOSPlatform("windows")]
    private static VerificationResult Verify(
        string path,
        uint signatureIndex,
        bool discoverCount)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var settingsPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<WinTrustSignatureSettings>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);
            var settings = WinTrustSignatureSettings.Create(
                signatureIndex,
                discoverCount
                    ? WinTrustSignatureFlags.GetSecondarySignatureCount
                    : WinTrustSignatureFlags.VerifySpecific);
            Marshal.StructureToPtr(settings, settingsPointer, fDeleteOld: false);
            var trustData = WinTrustData.ForFile(filePointer, settingsPointer);
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            try
            {
                settings = Marshal.PtrToStructure<WinTrustSignatureSettings>(
                    settingsPointer);
                if (status != 0 || trustData.StateData == IntPtr.Zero)
                {
                    throw new CryptographicException(
                        "Authenticode verification failed.");
                }

                var signers = ExtractSigners(trustData.StateData);
                return new VerificationResult(settings.SecondarySignatureCount, signers);
            }
            finally
            {
                trustData.StateAction = WinTrustStateAction.Close;
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustSignatureSettings>(settingsPointer);
            Marshal.FreeHGlobal(settingsPointer);
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AuthenticodeSignerIdentity> ExtractSigners(
        IntPtr stateData)
    {
        var providerData = WTHelperProvDataFromStateData(stateData);
        if (providerData == IntPtr.Zero)
        {
            throw new CryptographicException("Authenticode provider state is unavailable.");
        }

        var result = new List<AuthenticodeSignerIdentity>();
        for (uint index = 0; index < 16; index++)
        {
            var signer = WTHelperGetProvSignerFromChain(
                providerData,
                index,
                counterSigner: false,
                counterSignerIndex: 0);
            if (signer == IntPtr.Zero)
            {
                break;
            }

            var providerCertificate = WTHelperGetProvCertFromChain(signer, 0);
            if (providerCertificate == IntPtr.Zero)
            {
                throw new CryptographicException("Authenticode signer certificate is unavailable.");
            }

            var nativeCertificate =
                Marshal.PtrToStructure<CryptProviderCertificate>(providerCertificate);
            if (nativeCertificate.CertificateContext == IntPtr.Zero)
            {
                throw new CryptographicException("Authenticode signer certificate is invalid.");
            }

            using var certificate = new X509Certificate2(nativeCertificate.CertificateContext);
            var canonicalName = certificate.SubjectName.Decode(
                AuthenticodePublisherPolicy.CanonicalNameFlags);
            var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
            result.Add(new AuthenticodeSignerIdentity(
                canonicalName,
                SHA256.HashData(spki)));
        }

        return result.AsReadOnly();
    }

    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private enum WinTrustStateAction : uint
    {
        Verify = 1,
        Close = 2,
    }

    [Flags]
    private enum WinTrustSignatureFlags : uint
    {
        VerifySpecific = 1,
        GetSecondarySignatureCount = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustSignatureSettings
    {
        public uint StructureSize;
        public uint Index;
        public WinTrustSignatureFlags Flags;
        public uint SecondarySignatureCount;
        public uint VerifiedSignatureIndex;
        public IntPtr CryptoPolicy;

        public static WinTrustSignatureSettings Create(
            uint index,
            WinTrustSignatureFlags flags) =>
            new()
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustSignatureSettings>(),
                Index = index,
                Flags = flags,
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        public uint StructureSize;
        public IntPtr CertificateContext;
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

        public static WinTrustData ForFile(
            IntPtr fileInfo,
            IntPtr signatureSettings) =>
            new()
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = fileInfo,
                StateAction = WinTrustStateAction.Verify,
                ProviderFlags = 0x00000040,
                SignatureSettings = signatureSettings,
            };
    }

    private sealed record VerificationResult(
        uint SecondaryCount,
        IReadOnlyList<AuthenticodeSignerIdentity> Signers);

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(
        IntPtr signer,
        uint certificateIndex);
}
