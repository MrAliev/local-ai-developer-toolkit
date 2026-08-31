using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Tests;

public sealed class ModelProvisioningPlannerTests
{
    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    private static GpuSnapshot Adapter(ulong dedicatedBytes) =>
        new(
            ObservationState.Available,
            [new GpuAdapterSnapshot("adapter-1", "Test adapter", dedicatedBytes, false)],
            null);

    private static ManifestModel Model(string name, int context, ulong bytes) =>
        new(name, context, (long)bytes, (long)bytes);

    [Fact]
    public void Automatic_takes_the_largest_context_each_model_fits_in()
    {
        var plan = ModelProvisioningPlanner.Create(
            [
                Model("small:1b", 2048, Gigabyte),
                Model("small:1b", 32768, Gigabyte),
                Model("small:1b", 262144, Gigabyte),
            ],
            Adapter(16 * Gigabyte),
            new(ModelProvisioningMode.Automatic));

        var request = Assert.Single(plan.Requests);
        Assert.Equal("small:1b", request.Action.Model);
        // 1 GiB of weights, 1 GiB of runtime reserve and 256 KiB per token: 32768 tokens fit
        // inside 16 GiB, 262144 do not.
        Assert.Equal(32768, request.Action.ContextSize);
        Assert.True(request.Action.Selected);
        Assert.True(request.Action.ConsentGranted);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void Every_request_carries_the_whole_choice_set_for_one_adapter()
    {
        var plan = ModelProvisioningPlanner.Create(
            [
                Model("small:1b", 2048, Gigabyte),
                Model("other:2b", 2048, 2 * Gigabyte),
            ],
            Adapter(16 * Gigabyte),
            new(ModelProvisioningMode.Automatic));

        Assert.Equal(2, plan.Requests.Count);
        Assert.All(
            plan.Requests,
            request =>
            {
                // The broker installer refuses a request whose choices do not share one
                // budget, and uses the rest to suggest something smaller when it says no.
                Assert.Equal(2, request.FallbackChoices.Count);
                Assert.Single(request.FallbackChoices.Select(
                    choice => choice.AvailableDedicatedBytes).Distinct());
                Assert.Contains(
                    request.FallbackChoices,
                    choice =>
                        choice.Name == request.Action.Model &&
                        choice.ContextTokens == request.Action.ContextSize &&
                        choice.IsEnabled);
            });
        // Distinct action ids, which the installer requires.
        Assert.Equal(
            plan.Requests.Count,
            plan.Requests.Select(request => request.Action.ActionId).Distinct().Count());
    }

    [Fact]
    public void A_model_too_large_for_the_adapter_is_reported_rather_than_downloaded()
    {
        var plan = ModelProvisioningPlanner.Create(
            [
                Model("small:1b", 2048, Gigabyte),
                Model("huge:70b", 2048, 40 * Gigabyte),
            ],
            Adapter(8 * Gigabyte),
            new(ModelProvisioningMode.Automatic));

        var request = Assert.Single(plan.Requests);
        Assert.Equal("small:1b", request.Action.Model);
        Assert.Contains("huge:70b", Assert.Single(plan.Excluded), StringComparison.Ordinal);
    }

    [Fact]
    public void An_exact_choice_outside_the_signed_manifest_is_refused()
    {
        var plan = ModelProvisioningPlanner.Create(
            [Model("small:1b", 2048, Gigabyte)],
            Adapter(16 * Gigabyte),
            new(ModelProvisioningMode.Exact, "small:1b", 32768));

        Assert.Empty(plan.Requests);
        Assert.Contains(
            "not in the signed release manifest",
            Assert.Single(plan.Excluded),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_exact_choice_inside_the_manifest_becomes_the_only_request()
    {
        var plan = ModelProvisioningPlanner.Create(
            [
                Model("small:1b", 2048, Gigabyte),
                Model("other:2b", 2048, 2 * Gigabyte),
            ],
            Adapter(16 * Gigabyte),
            new(ModelProvisioningMode.Exact, "other:2b", 2048));

        var request = Assert.Single(plan.Requests);
        Assert.Equal("other:2b", request.Action.Model);
        Assert.Equal(2048, request.Action.ContextSize);
    }

    [Fact]
    public void Skipping_models_asks_for_nothing_at_all()
    {
        var plan = ModelProvisioningPlanner.Create(
            [Model("small:1b", 2048, Gigabyte)],
            Adapter(16 * Gigabyte),
            ModelProvisioningSelection.None);

        Assert.Empty(plan.Requests);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void A_release_without_signed_models_says_so_instead_of_guessing()
    {
        var plan = ModelProvisioningPlanner.Create(
            [],
            Adapter(16 * Gigabyte),
            new(ModelProvisioningMode.Automatic));

        Assert.Empty(plan.Requests);
        Assert.Contains(
            "lists no models",
            Assert.Single(plan.Excluded),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_usable_adapter_nothing_is_downloaded()
    {
        var plan = ModelProvisioningPlanner.Create(
            [Model("small:1b", 2048, Gigabyte)],
            new GpuSnapshot(ObservationState.Unavailable, [], "no adapter"),
            new(ModelProvisioningMode.Automatic));

        Assert.Empty(plan.Requests);
        Assert.Single(plan.Excluded);
    }
}
