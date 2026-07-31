using System.Runtime.InteropServices;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class DxgiNativeGpuAdapterEnumerator : INativeGpuAdapterEnumerator
{
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private static readonly Guid IdxgiFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    public NativeGpuEnumeration Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NativeGpuEnumeration(
                ObservationState.Unsupported,
                [],
                "DXGI is only available on Windows.");
        }

        IntPtr factory = IntPtr.Zero;
        try
        {
            var factoryId = IdxgiFactory1;
            var result = CreateDXGIFactory1(ref factoryId, out factory);
            Marshal.ThrowExceptionForHR(result);
            return EnumerateAdapters(factory);
        }
        catch (Exception exception) when (
            exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new NativeGpuEnumeration(
                ObservationState.Failed,
                [],
                $"DXGI adapter enumeration failed: {exception.Message}");
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }
        }
    }

    private static NativeGpuEnumeration EnumerateAdapters(IntPtr factory)
    {
        var adapters = new List<NativeGpuAdapterDescriptor>();
        var enumerate = GetVTableDelegate<EnumAdapters1Delegate>(factory, 12);
        for (uint index = 0; ; index++)
        {
            var result = enumerate(factory, index, out var adapter);
            if (result == DxgiErrorNotFound)
            {
                break;
            }

            Marshal.ThrowExceptionForHR(result);
            try
            {
                var getDescription = GetVTableDelegate<GetDesc1Delegate>(adapter, 10);
                Marshal.ThrowExceptionForHR(getDescription(adapter, out var description));
                adapters.Add(new NativeGpuAdapterDescriptor(
                    $"LUID:{description.AdapterLuid.HighPart:X8}{description.AdapterLuid.LowPart:X8}",
                    description.Description.TrimEnd('\0'),
                    description.DedicatedVideoMemory,
                    (description.Flags & DxgiAdapterFlagSoftware) != 0));
            }
            finally
            {
                Marshal.Release(adapter);
            }
        }

        return new NativeGpuEnumeration(
            ObservationState.Available,
            adapters,
            null);
    }

    private static T GetVTableDelegate<T>(IntPtr instance, int methodIndex)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var method = Marshal.ReadIntPtr(vtable, methodIndex * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(
        ref Guid riid,
        out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(
        IntPtr factory,
        uint adapter,
        out IntPtr adapterPointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(
        IntPtr adapter,
        out DxgiAdapterDescription1 description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }
}
