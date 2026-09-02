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
    /// <summary>For a window. Follows the language the installer was told to speak.</summary>
    public static string Describe(GpuAdapterSnapshot adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var memory = adapter.DedicatedLocalBytes > 0
            ? string.Format(
                InstallerCulture.Pick(
                    " ({0:N1} GB dedicated)",
                    " ({0:N1} ГБ выделенной памяти)"),
                adapter.DedicatedLocalBytes / (1024d * 1024 * 1024))
            : InstallerCulture.Pick(
                " (no dedicated memory)",
                " (выделенной памяти нет)");
        var software = adapter.IsSoftware
            ? InstallerCulture.Pick(" [software]", " [программный]")
            : string.Empty;
        return adapter.Name + memory + software;
    }

    /// <summary>
    /// For a sentence that is written to the run log. The log sits beside an English journal
    /// and stays English, so a Russian parenthetical in the middle of it helps nobody.
    /// </summary>
    public static string DescribeInEnglish(GpuAdapterSnapshot adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var memory = adapter.DedicatedLocalBytes > 0
            ? FormattableString.Invariant(
                $" ({adapter.DedicatedLocalBytes / (1024d * 1024 * 1024):N1} GB dedicated)")
            : " (no dedicated memory)";
        return adapter.Name + memory + (adapter.IsSoftware ? " [software]" : string.Empty);
    }
}
