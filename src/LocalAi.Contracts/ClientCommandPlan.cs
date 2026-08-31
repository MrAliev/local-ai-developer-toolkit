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
public enum McpToolApproval
{
    /// <summary>Runs without a prompt: reads, or bounded local compute with no persistent state.</summary>
    Approve,

    /// <summary>Asks first: network downloads, persistent state, and anything unclassified.</summary>
    Prompt,
}

public sealed record McpToolPolicy(string Name, McpToolApproval Approval);

/// <summary>
/// Every tool each managed MCP server exposes, with the approval each one deserves.
///
/// Auto-approving the whole namespace was deliberate UX — the product exists so an agent can
/// call search and the local helpers dozens of times without a prompt — but the list is not
/// read-only, and the server-wide approve also pre-approved every future tool sight unseen
/// (#208). The classification is least-privilege where it costs nothing: the two tools that
/// download gigabytes or change persistent routing state prompt, everything that reads or
/// runs bounded local compute does not, and a tool absent from this matrix both fails the
/// inventory tests and — through the server default of prompt — asks at runtime until a
/// release classifies it.
/// </summary>
public static class McpToolNames
{
    public static IReadOnlyList<McpToolPolicy> CodeSearchPolicies { get; } =
    [
        new("search_code", McpToolApproval.Approve),
        new("index_status", McpToolApproval.Approve),
        // Mutates the index, but bounded by construction: large work is refused inline and
        // returned as a command instead, so what runs here is a post-commit delta.
        new("index_refresh", McpToolApproval.Approve),
        new("index_unload", McpToolApproval.Approve),
        new("get_code_chunk", McpToolApproval.Approve),
        new("go_to_definition", McpToolApproval.Approve),
        new("find_references", McpToolApproval.Approve),
        new("find_implementations", McpToolApproval.Approve),
        new("find_relationships", McpToolApproval.Approve),
        // Session-scoped, and inert until the user opts into live servers in their own
        // settings file — that opt-in is the real approval.
        new("lsp_open_document", McpToolApproval.Approve),
        new("lsp_close_document", McpToolApproval.Approve),
    ];

    public static IReadOnlyList<McpToolPolicy> LocalLmPolicies { get; } =
    [
        new("ask_local", McpToolApproval.Approve),
        new("translate_local", McpToolApproval.Approve),
        new("triage_log", McpToolApproval.Approve),
        new("read_image", McpToolApproval.Approve),
        new("local_models_status", McpToolApproval.Approve),
        // Downloads models by the gigabyte: the one network-and-disk heavyweight here.
        new("local_models_sync", McpToolApproval.Prompt),
        new("local_model_preflight", McpToolApproval.Approve),
        // An owner's decision that changes persistent experiment and routing state.
        new("local_model_feedback", McpToolApproval.Prompt),
        new("local_model_experiment_report", McpToolApproval.Approve),
    ];

    public static IReadOnlyList<string> CodeSearch { get; } =
        CodeSearchPolicies.Select(policy => policy.Name).ToArray();

    public static IReadOnlyList<string> LocalLm { get; } =
        LocalLmPolicies.Select(policy => policy.Name).ToArray();

    public static McpToolApproval ApprovalFor(string server, string tool)
    {
        var policies = server switch
        {
            "codesearch" => CodeSearchPolicies,
            "locallm" => LocalLmPolicies,
            _ => throw new ArgumentException($"Unknown managed server '{server}'.", nameof(server)),
        };
        return policies.SingleOrDefault(policy =>
                string.Equals(policy.Name, tool, StringComparison.Ordinal))?.Approval
            ?? McpToolApproval.Prompt;
    }
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
        "default_tools_approval_mode = \"prompt\"";
}
