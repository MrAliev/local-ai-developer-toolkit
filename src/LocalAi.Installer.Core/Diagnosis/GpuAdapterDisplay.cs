namespace LocalAi.Installer.Core.Diagnosis;

/// <summary>
/// How a graphics adapter is named to a person.
///
/// One formatter, because there were two descriptions of the same adapter and only one of them
/// was readable: the system-check page said "NVIDIA GeForce RTX 5080 (16.3 GB dedicated)" while
/// the model recommendation said "Using dedicated GPU adapter 'PCI\VEN_10DE&amp;DEV_2C02&amp;…'",
/// and the second one is what reached the models page.
///
/// The name comes from DXGI's adapter description, and the memory from its dedicated video
/// memory — not from WMI, whose <c>AdapterRAM</c> is a 32-bit field that reports 4 GB for every
/// card above that.
/// </summary>
public static class GpuAdapterDisplay
{
    public static string Describe(GpuAdapterSnapshot adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var memory = adapter.DedicatedLocalBytes > 0
            ? $" ({adapter.DedicatedLocalBytes / (1024d * 1024 * 1024):N1} GB dedicated)"
            : " (no dedicated memory)";
        return adapter.Name + memory + (adapter.IsSoftware ? " [software]" : string.Empty);
    }
}
