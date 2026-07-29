using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

public sealed class ClientRegistrationTests
{
    [Fact]
    public void Claude_and_codex_receive_the_same_launcher_registrations()
    {
        var plan = ClientCommand.Plan(@"C:\LocalAi\bin");

        Assert.Equal(
            @"C:\LocalAi\bin\launcher\localai-launcher.exe",
            plan.CodeSearch.Command);
        Assert.Equal(["run", "codesearch-mcp"], plan.CodeSearch.Arguments);
        Assert.Equal(plan.CodeSearch.Command, plan.LocalLm.Command);
        Assert.Equal(["run", "locallm-mcp"], plan.LocalLm.Arguments);
        Assert.Contains(
            "args = [\"run\", \"codesearch-mcp\"]",
            plan.CodexTomlSections[0],
            StringComparison.Ordinal);
        Assert.Contains(
            plan.ClaudeCommands,
            command => command.Contains(
                "-- \"C:\\LocalAi\\bin\\launcher\\localai-launcher.exe\" " +
                "run codesearch-mcp",
                StringComparison.Ordinal));
        Assert.Contains(
            plan.ClaudeCommands,
            command => command.Contains(
                "-- \"C:\\LocalAi\\bin\\launcher\\localai-launcher.exe\" " +
                "run locallm-mcp",
                StringComparison.Ordinal));
        Assert.True(plan.RequiresClientRestart);
        Assert.True(plan.IncludesEmbeddedRoutingCatalog);
        Assert.True(plan.PreservesExistingModels);
        Assert.Equal("local_models_sync", plan.RecommendedModelSyncTool);
        Assert.False(plan.AppliesClientConfiguration);
        Assert.Equal(3, ClientCommand.McpFallbackChoices().Count);
    }
}
