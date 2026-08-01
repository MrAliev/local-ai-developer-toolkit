using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Tests;

public sealed class WindowsGpuProbeTests
{
    [Fact]
    public async Task Keeps_each_physical_adapter_memory_separate_and_excludes_software()
    {
        var native = new FakeNativeGpuAdapterEnumerator(
        [
            new NativeGpuAdapterDescriptor("luid-1", "GPU A", 8_000, false),
            new NativeGpuAdapterDescriptor("luid-2", "GPU B", 16_000, false),
            new NativeGpuAdapterDescriptor("warp", "Microsoft Basic Render", 64_000, true),
        ]);
        var probe = new WindowsGpuProbe(native);

        var snapshot = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ObservationState.Available, snapshot.State);
        Assert.Collection(
            snapshot.Adapters,
            adapter =>
            {
                Assert.Equal("luid-1", adapter.StableId);
                Assert.Equal(8_000UL, adapter.DedicatedLocalBytes);
                Assert.False(adapter.IsSoftware);
            },
            adapter =>
            {
                Assert.Equal("luid-2", adapter.StableId);
                Assert.Equal(16_000UL, adapter.DedicatedLocalBytes);
                Assert.False(adapter.IsSoftware);
            });
        Assert.DoesNotContain(snapshot.Adapters, adapter => adapter.StableId == "warp");
    }

    [Fact]
    public async Task Preserves_unsupported_native_result_as_empty()
    {
        var probe = new WindowsGpuProbe(
            new FakeNativeGpuAdapterEnumerator(
                [],
                ObservationState.Unsupported,
                "DXGI is only available on Windows."));

        var snapshot = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ObservationState.Unsupported, snapshot.State);
        Assert.Empty(snapshot.Adapters);
        Assert.NotNull(snapshot.Reason);
    }

    [Fact]
    public void Production_DXGI_enumeration_is_safe_and_repeatable_without_GPU_assumptions()
    {
        var enumerator = new DxgiNativeGpuAdapterEnumerator();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = enumerator.Enumerate();

            Assert.Contains(
                result.State,
                new[]
                {
                    ObservationState.Available,
                    ObservationState.Failed,
                    ObservationState.Unsupported,
                });
            Assert.All(
                result.Adapters,
                adapter =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(adapter.StableId));
                    Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
                });
        }
    }

    private sealed class FakeNativeGpuAdapterEnumerator(
        IReadOnlyList<NativeGpuAdapterDescriptor> adapters,
        ObservationState state = ObservationState.Available,
        string? reason = null) : INativeGpuAdapterEnumerator
    {
        public NativeGpuEnumeration Enumerate() => new(state, adapters, reason);
    }
}
