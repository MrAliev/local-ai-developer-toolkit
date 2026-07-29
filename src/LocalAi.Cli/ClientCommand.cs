namespace LocalAi.Cli;

public sealed record ClientToolRegistration(
    string Command,
    IReadOnlyList<string> Arguments);

public sealed record ClientRegistrationPlan(
    ClientToolRegistration CodeSearch,
    ClientToolRegistration LocalLm,
    IReadOnlyList<string> ClaudeCommands,
    IReadOnlyList<string> CodexTomlSections,
    bool RequiresClientRestart,
    bool IncludesEmbeddedRoutingCatalog,
    bool PreservesExistingModels,
    string RecommendedModelSyncTool,
    bool AppliesClientConfiguration);

public static class ClientCommand
{
    public static ClientRegistrationPlan Plan(string installationDirectory)
    {
        var root = Path.GetFullPath(installationDirectory);
        var launcher = Path.Combine(
            root,
            "launcher",
            "localai-launcher.exe");
        var codeSearch = new ClientToolRegistration(
            launcher,
            ["run", "codesearch-mcp"]);
        var localLm = new ClientToolRegistration(
            launcher,
            ["run", "locallm-mcp"]);
        return new ClientRegistrationPlan(
            codeSearch,
            localLm,
            [
                "claude mcp remove codesearch -s user",
                "claude mcp remove locallm -s user",
                $"claude mcp add codesearch -s user -- \"{launcher}\" " +
                string.Join(' ', codeSearch.Arguments),
                $"claude mcp add locallm -s user -- \"{launcher}\" " +
                string.Join(' ', localLm.Arguments)
            ],
            [
                TomlSection("codesearch", codeSearch),
                TomlSection("locallm", localLm)
            ],
            RequiresClientRestart: true,
            IncludesEmbeddedRoutingCatalog: true,
            PreservesExistingModels: true,
            RecommendedModelSyncTool: "local_models_sync",
            AppliesClientConfiguration: false);
    }

    public static IReadOnlyList<string> McpFallbackChoices() =>
    [
        "Diagnose or restart MCP.",
        "Use the LocalAi CLI through the same broker.",
        "Continue without local models using rg."
    ];

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string TomlSection(
        string name,
        ClientToolRegistration registration) =>
        $"[mcp_servers.{name}]\n" +
        $"command = \"{EscapeToml(registration.Command)}\"\n" +
        "args = [" +
        string.Join(
            ", ",
            registration.Arguments.Select(
                argument => $"\"{EscapeToml(argument)}\"")) +
        "]";
}
