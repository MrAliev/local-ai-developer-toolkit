namespace LocalAi.Cli;

public sealed record ClientRegistrationPlan(
    string CodeSearchBinary,
    string LocalLmBinary,
    IReadOnlyList<string> ClaudeCommands,
    IReadOnlyList<string> CodexTomlSections,
    bool RequiresClientRestart);

public static class ClientCommand
{
    public static ClientRegistrationPlan Plan(string installationDirectory)
    {
        var root = Path.GetFullPath(installationDirectory);
        var codeSearch = Path.Combine(root, "codesearch-mcp.exe");
        var localLm = Path.Combine(root, "locallm-mcp.exe");
        return new ClientRegistrationPlan(
            codeSearch,
            localLm,
            [
                "claude mcp remove codesearch -s user",
                "claude mcp remove locallm -s user",
                $"claude mcp add codesearch -s user -- \"{codeSearch}\"",
                $"claude mcp add locallm -s user -- \"{localLm}\""
            ],
            [
                $"[mcp_servers.codesearch]\ncommand = \"{EscapeToml(codeSearch)}\"",
                $"[mcp_servers.locallm]\ncommand = \"{EscapeToml(localLm)}\""
            ],
            RequiresClientRestart: true);
    }

    public static IReadOnlyList<string> McpFallbackChoices() =>
    [
        "Diagnose or restart MCP.",
        "Use the LocalAi CLI through the same broker.",
        "Continue without local models using rg."
    ];

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
