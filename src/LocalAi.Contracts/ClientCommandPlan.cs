namespace LocalAi.Contracts;

public sealed record ClientToolRegistration(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Tools);

/// <summary>
/// Every tool each managed MCP server exposes.
///
/// Codex records per-tool settings under <c>[mcp_servers.&lt;server&gt;.tools.&lt;tool&gt;]</c>, and
/// a machine that had been running LocalAi since before precise navigation and the model
/// experiment tools shipped carried entries for eleven of the twenty — the eleven that had been
/// reached for at least once. The other nine simply had no row.
///
/// Kept here rather than read from the server assemblies because the installer configures clients
/// without loading either server. That makes drift possible, so it is a test rather than a
/// convention that holds the two together: <c>CodeSearch.Tests</c> and <c>LocalLm.Tests</c> each
/// reflect over their own tool attributes and fail if this list disagrees.
/// </summary>
public static class McpToolNames
{
    public static IReadOnlyList<string> CodeSearch { get; } =
    [
        "search_code",
        "index_status",
        "index_refresh",
        "index_unload",
        "get_code_chunk",
        "go_to_definition",
        "find_references",
        "find_implementations",
        "find_relationships",
        "lsp_open_document",
        "lsp_close_document",
    ];

    public static IReadOnlyList<string> LocalLm { get; } =
    [
        "ask_local",
        "translate_local",
        "triage_log",
        "read_image",
        "local_models_status",
        "local_models_sync",
        "local_model_preflight",
        "local_model_feedback",
        "local_model_experiment_report",
    ];
}

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

public static class ClientCommandPlan
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
            ["run", "codesearch-mcp"],
            McpToolNames.CodeSearch);
        var localLm = new ClientToolRegistration(
            launcher,
            ["run", "locallm-mcp"],
            McpToolNames.LocalLm);
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
        "]\n" +
        "default_tools_approval_mode = \"approve\"";
}
