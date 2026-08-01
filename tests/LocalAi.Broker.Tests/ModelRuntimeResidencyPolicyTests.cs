using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The residency policy is the one switch that lets LocalAi run on a machine without a
/// discrete adapter, so both what it admits and what it still refuses are pinned here.
/// </summary>
public sealed class ModelRuntimeResidencyPolicyTests
{
    private const string Model = "translategemma:12b";
    private const long Size = 8_109_818_272;

    private static ModelRuntime CreateRuntime(
        long sizeVramBytes,
        ModelResidencyPolicy policy,
        out ModelRuntimeTests.FakeRuntimeTransport transport)
    {
        transport = new ModelRuntimeTests.FakeRuntimeTransport
        {
            Installed = [Model],
            Processes =
            [
                new OllamaProcessInfo(
                    Model,
                    Size,
                    sizeVramBytes,
                    2048,
                    DateTimeOffset.UtcNow.AddMinutes(30))
            ]
        };

        return new ModelRuntime(
            transport,
            ModelRoutingCatalog.LoadEmbedded(),
            new ModelRuntimeTests.FixedTimeProvider(
                new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero)),
            policy);
    }

    [Fact]
    public async Task Strict_policy_still_refuses_a_partially_offloaded_model()
    {
        var runtime = CreateRuntime(
            Size / 2,
            ModelResidencyPolicy.RequireFullVram,
            out _);

        var error = await Assert.ThrowsAsync<ModelPreflightException>(
            () => runtime.EnsureReadyAsync(Model, 2048, TestContext.Current.CancellationToken));

        Assert.Equal(ModelExecutionOutcome.CpuOffload, error.Outcome);
    }

    [Fact]
    public async Task Partial_policy_admits_a_partly_offloaded_model_and_says_so()
    {
        var runtime = CreateRuntime(
            Size / 2,
            ModelResidencyPolicy.AllowPartialOffload,
            out _);

        var proof = await runtime.EnsureReadyAsync(
            Model,
            2048,
            TestContext.Current.CancellationToken);

        Assert.False(proof.FullyResident);
        Assert.NotNull(proof.DegradationWarning);
        Assert.Contains("50%", proof.DegradationWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_policy_still_refuses_a_pure_cpu_load()
    {
        // Choosing "some GPU" must not silently become "no GPU at all".
        var runtime = CreateRuntime(0, ModelResidencyPolicy.AllowPartialOffload, out _);

        await Assert.ThrowsAsync<ModelPreflightException>(
            () => runtime.EnsureReadyAsync(Model, 2048, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cpu_policy_admits_a_cpu_load_and_says_so()
    {
        var runtime = CreateRuntime(0, ModelResidencyPolicy.AllowCpu, out _);

        var proof = await runtime.EnsureReadyAsync(
            Model,
            2048,
            TestContext.Current.CancellationToken);

        Assert.False(proof.FullyResident);
        Assert.NotNull(proof.DegradationWarning);
        Assert.Contains("CPU", proof.DegradationWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fully_resident_load_carries_no_warning_under_a_relaxed_policy()
    {
        var runtime = CreateRuntime(Size, ModelResidencyPolicy.AllowCpu, out _);

        var proof = await runtime.EnsureReadyAsync(
            Model,
            2048,
            TestContext.Current.CancellationToken);

        Assert.True(proof.FullyResident);
        Assert.Null(proof.DegradationWarning);
    }

    [Fact]
    public async Task A_model_that_reports_no_size_is_refused_by_every_policy()
    {
        // Nothing actually loaded; relaxing residency must not turn this into a success.
        var runtime = CreateRuntime(0, ModelResidencyPolicy.AllowCpu, out var transport);
        transport.Processes =
        [
            new OllamaProcessInfo(Model, 0, 0, 2048, DateTimeOffset.UtcNow.AddMinutes(30))
        ];

        var error = await Assert.ThrowsAsync<ModelPreflightException>(
            () => runtime.EnsureReadyAsync(Model, 2048, TestContext.Current.CancellationToken));

        Assert.Equal(ModelExecutionOutcome.TechnicalFailure, error.Outcome);
    }
}
