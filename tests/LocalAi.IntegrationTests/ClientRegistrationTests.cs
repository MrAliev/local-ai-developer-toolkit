using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

public sealed class ClientRegistrationTests
{
    [Fact]
    public void Claude_and_codex_receive_the_same_binaries()
    {
        var plan = ClientCommand.Plan(@"C:\LocalAi\bin");

        Assert.Contains(
            plan.ClaudeCommands,
            command => command.Contains(plan.CodeSearchBinary, StringComparison.Ordinal));
        Assert.Contains(
            plan.ClaudeCommands,
            command => command.Contains(plan.LocalLmBinary, StringComparison.Ordinal));
        Assert.Contains(
            plan.CodexTomlSections,
            section => section.Contains(
                plan.CodeSearchBinary.Replace("\\", "\\\\"),
                StringComparison.Ordinal));
        Assert.True(plan.RequiresClientRestart);
        Assert.True(plan.IncludesEmbeddedRoutingCatalog);
        Assert.True(plan.PreservesExistingModels);
        Assert.Equal("local_models_sync", plan.RecommendedModelSyncTool);
        Assert.False(plan.AppliesClientConfiguration);
        Assert.Equal(3, ClientCommand.McpFallbackChoices().Count);
    }
}
