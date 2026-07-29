using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelRoutingCatalogTests
{
    [Fact]
    public void Embedded_catalog_is_complete_and_uses_supported_context_tiers()
    {
        var catalog = ModelRoutingCatalog.LoadEmbedded();

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal("1", catalog.CatalogVersion);
        Assert.Equal(
            Enum.GetValues<LocalTaskProfile>().Order(),
            catalog.Routes.Select(route => route.Profile).Order());

        var supportedContexts = new HashSet<int>
        {
            2048,
            4096,
            8192,
            16384,
            32768,
            65536,
            131072,
            262144
        };
        Assert.All(
            catalog.Models,
            model => Assert.All(
                model.ContextTokens,
                context => Assert.Contains(context, supportedContexts)));
        Assert.Equal(
            32768,
            catalog.Model("qwen2.5-coder:14b").ContextTokens.Max());
        Assert.Equal(
            131072,
            catalog.Model("gpt-oss:20b").ContextTokens.Max());
        Assert.Equal(
            262144,
            catalog.Model("qwen3.5:9b").ContextTokens.Max());
        Assert.All(
            catalog.Routes.Where(route => route.Mode == LocalRouteMode.Model),
            route =>
            {
                Assert.NotEmpty(route.Candidates);
                Assert.NotEmpty(route.Fallbacks);
            });
    }

    [Fact]
    public void Catalog_pins_translation_embedding_and_deterministic_search_routes()
    {
        var catalog = ModelRoutingCatalog.LoadEmbedded();

        var translate = catalog.Route(LocalTaskProfile.PlainTranslation);
        Assert.Equal("translategemma:12b", Assert.Single(translate.Candidates));
        Assert.Equal(["qwen3.5:9b"], translate.Fallbacks);
        var translateModel = catalog.Model("translategemma:12b");
        Assert.Equal(LocalModelLifecycle.Experimental, translateModel.Lifecycle);
        Assert.Equal(LocalModelInstallPolicy.Recommended, translateModel.InstallPolicy);

        var embedding = catalog.Route(LocalTaskProfile.VectorEmbedding);
        Assert.Equal(["qwen3-embedding:8b-q8_0"], embedding.Candidates);
        Assert.Equal(["qwen3-embedding:8b-q8_0"], embedding.Fallbacks);

        var exact = catalog.Route(LocalTaskProfile.ExactSearch);
        Assert.Equal(LocalRouteMode.Deterministic, exact.Mode);
        Assert.Empty(exact.Candidates);
        Assert.Empty(exact.Fallbacks);
    }

    [Fact]
    public void Maintenance_allowlist_contains_only_catalog_recommended_or_experimental_tags()
    {
        var catalog = ModelRoutingCatalog.LoadEmbedded();

        Assert.True(catalog.IsMaintenanceAllowed("translategemma:12b"));
        Assert.False(catalog.IsMaintenanceAllowed("untrusted/model:latest"));
        Assert.All(
            catalog.MaintenanceAllowlist,
            tag =>
            {
                var model = catalog.Model(tag);
                Assert.True(
                    model.Lifecycle == LocalModelLifecycle.Experimental ||
                    model.InstallPolicy == LocalModelInstallPolicy.Recommended);
            });
    }
}
