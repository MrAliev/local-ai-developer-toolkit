using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LocalAi.Contracts.Activation;

internal static class SecureNamedMutexName
{
    public static string Create(string prefix, string scope)
    {
        var normalized = OperatingSystem.IsWindows()
            ? scope.ToUpperInvariant()
            : scope;
        var scopeHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalized)));
        if (!OperatingSystem.IsWindows())
        {
            return prefix + scopeHash;
        }

        var user = WindowsIdentity.GetCurrent().User?.Value ??
            throw new UnauthorizedAccessException("The current Windows identity is unavailable.");
        var userHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(user)));
        return @"Local\" + prefix + userHash + "." + scopeHash;
    }
}

internal interface ISecureNamedMutexFactory
{
    ISecureNamedMutex Create(string name);
}

internal interface ISecureNamedMutex : IDisposable
{
    bool WaitOne(TimeSpan timeout);
    void Release();
}

internal sealed class SecureNamedMutexFactory : ISecureNamedMutexFactory
{
    public static SecureNamedMutexFactory Instance { get; } = new();

    public ISecureNamedMutex Create(string name) =>
        OperatingSystem.IsWindows()
            ? WindowsSecureNamedMutex.Create(name)
            : new ManagedSecureNamedMutex(name);

    private sealed class ManagedSecureNamedMutex(string name) : ISecureNamedMutex
    {
        private readonly Mutex mutex = new(false, name);

        public bool WaitOne(TimeSpan timeout)
        {
            try
            {
                return mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        public void Release() => mutex.ReleaseMutex();
        public void Dispose() => mutex.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsSecureNamedMutex : ISecureNamedMutex
{
    private const uint MutexAllAccess = 0x001F0001;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitAbandoned = 0x00000080;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private readonly SafeWaitHandle handle;

    private WindowsSecureNamedMutex(SafeWaitHandle handle)
    {
        this.handle = handle;
    }

    public static WindowsSecureNamedMutex Create(string name)
    {
        var descriptor = CreateDescriptor();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = false,
            };
            var handle = CreateMutexW(ref attributes, false, name);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            try
            {
                ValidateDescriptor(handle);
                var result = new WindowsSecureNamedMutex(handle);
                handle = null!;
                return result;
            }
            finally
            {
                handle?.Dispose();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorPointer);
        }
    }

    public bool WaitOne(TimeSpan timeout)
    {
        var milliseconds = timeout == Timeout.InfiniteTimeSpan
            ? uint.MaxValue
            : checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
        var result = WaitForSingleObject(handle, milliseconds);
        return result switch
        {
            WaitObject0 or WaitAbandoned => true,
            WaitTimeout => false,
            WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error()),
            _ => throw new Win32Exception("Unexpected named mutex wait result."),
        };
    }

    public void Release()
    {
        if (!ReleaseMutex(handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose() => handle.Dispose();

    private static byte[] CreateDescriptor()
    {
        var user = CurrentUser();
        var descriptor = new RawSecurityDescriptor(
            $"O:{user.Value}G:{user.Value}D:P" +
            $"(A;;0x{MutexAllAccess:X};;;{user.Value})" +
            $"(A;;0x{MutexAllAccess:X};;;SY)" +
            $"(A;;0x{MutexAllAccess:X};;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void ValidateDescriptor(SafeWaitHandle handle)
    {
        _ = GetKernelObjectSecurity(
            handle,
            OwnerSecurityInformation | DaclSecurityInformation,
            null,
            0,
            out var required);
        if (required == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var bytes = new byte[required];
        if (!GetKernelObjectSecurity(
                handle,
                OwnerSecurityInformation | DaclSecurityInformation,
                bytes,
                (uint)bytes.Length,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var descriptor = new RawSecurityDescriptor(bytes, 0);
        var user = CurrentUser();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            user.Value,
            system.Value,
            administrators.Value,
        };
        if (descriptor.Owner != user ||
            !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent) ||
            !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) ||
            descriptor.DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException("The named mutex security descriptor is incompatible.");
        }

        var granted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not QualifiedAce qualified ||
                qualified.AceQualifier != AceQualifier.AccessAllowed ||
                qualified.SecurityIdentifier is null ||
                !allowed.Contains(qualified.SecurityIdentifier.Value))
            {
                throw new UnauthorizedAccessException("The named mutex security descriptor is incompatible.");
            }

            granted[qualified.SecurityIdentifier.Value] =
                granted.GetValueOrDefault(qualified.SecurityIdentifier.Value) |
                qualified.AccessMask;
        }

        if (allowed.Any(sid =>
                !granted.TryGetValue(sid, out var mask) ||
                (mask & MutexAllAccess) != MutexAllAccess))
        {
            throw new UnauthorizedAccessException("The named mutex security descriptor is incompatible.");
        }
    }

    private static SecurityIdentifier CurrentUser() =>
        WindowsIdentity.GetCurrent().User ??
        throw new UnauthorizedAccessException("The current Windows identity is unavailable.");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexW(
        ref SecurityAttributes mutexAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool initialOwner,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(SafeWaitHandle handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeWaitHandle handle,
        uint requestedInformation,
        byte[]? securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
}
