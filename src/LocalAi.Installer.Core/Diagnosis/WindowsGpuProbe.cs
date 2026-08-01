using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed record NativeGpuAdapterDescriptor(
    string StableId,
    string Name,
    ulong DedicatedLocalBytes,
    bool IsSoftware);

public sealed record NativeGpuEnumeration
{
    public NativeGpuEnumeration(
        ObservationState state,
        IEnumerable<NativeGpuAdapterDescriptor> adapters,
        string? reason)
    {
        State = state;
        Adapters = Array.AsReadOnly(adapters.ToArray());
        Reason = reason;
    }

    public ObservationState State { get; }
    public IReadOnlyList<NativeGpuAdapterDescriptor> Adapters { get; }
    public string? Reason { get; }
}

public interface INativeGpuAdapterEnumerator
{
    NativeGpuEnumeration Enumerate();
}

public sealed class WindowsGpuProbe(
    INativeGpuAdapterEnumerator nativeEnumerator) : IWindowsGpuProbe
{
    public Task<GpuSnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var native = nativeEnumerator.Enumerate();
        var physicalAdapters = native.Adapters
            .Where(adapter => !adapter.IsSoftware)
            .Select(adapter => new GpuAdapterSnapshot(
                adapter.StableId,
                adapter.Name,
                adapter.DedicatedLocalBytes,
                false))
            .ToArray();
        var state = native.State == ObservationState.Available &&
                    physicalAdapters.Length == 0
            ? ObservationState.Unavailable
            : native.State;
        var reason = state == ObservationState.Unavailable && native.Reason is null
            ? "DXGI reported no physical GPU adapters."
            : native.Reason;
        return Task.FromResult(new GpuSnapshot(state, physicalAdapters, reason));
    }
}
