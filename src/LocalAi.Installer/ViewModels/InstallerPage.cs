using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Wizard pages in navigation order. Progress is a page of its own so the confirmation page
/// stays a pure milestone — summary and consent — as the Windows wizard guidelines require.
/// </summary>
public enum InstallerPage
{
    Diagnose,
    Dependencies,
    Package,
    Models,
    Residency,
    Agents,
    Confirm,
    Progress,
    Finish,
}

public enum ModelSelectionMode
{
    Automatic,

    /// <summary>
    /// Pick one catalogue model and one of the context sizes it permits. This replaced a
    /// free-text field that accepted any string: a model outside the routing catalogue
    /// cannot be loaded, so that field could only ever configure a failure.
    /// </summary>
    ChooseExact,

    Skip,
}

/// <summary>
/// Integration choice per client app, mapped one-to-one onto
/// <see cref="AgentIntegrationChoice"/>. The wizard does not invent its own vocabulary, so
/// there is exactly one way to say "leave this client alone".
/// </summary>
public enum AgentChoice
{
    McpOnly,
    InstructionsOnly,
    McpAndInstructions,
    NoChange,
}

public static class AgentChoiceMapping
{
    public static AgentIntegrationChoice ToCore(this AgentChoice choice) =>
        choice switch
        {
            AgentChoice.McpOnly => AgentIntegrationChoice.McpOnly,
            AgentChoice.InstructionsOnly => AgentIntegrationChoice.InstructionsOnly,
            AgentChoice.McpAndInstructions => AgentIntegrationChoice.McpAndInstructions,
            AgentChoice.NoChange => AgentIntegrationChoice.NoChange,
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, null),
        };

    public static string Title(this AgentChoice choice) =>
        choice switch
        {
            AgentChoice.McpOnly => "Register MCP servers",
            AgentChoice.InstructionsOnly => "Install instructions block",
            AgentChoice.McpAndInstructions => "Register MCP servers and install instructions",
            AgentChoice.NoChange => "Leave unchanged",
            _ => string.Empty,
        };

    public static string Description(this AgentChoice choice) =>
        choice switch
        {
            AgentChoice.McpOnly =>
                "Adds the LocalAi code search and local model servers to this client.",
            AgentChoice.InstructionsOnly =>
                "Adds the managed LocalAi instructions block, without touching server registrations.",
            AgentChoice.McpAndInstructions =>
                "Both of the above. This is what a first-time setup normally wants.",
            AgentChoice.NoChange =>
                "This client is left exactly as it is.",
            _ => string.Empty,
        };
}

public enum CheckStatus
{
    Ok,
    Warning,
    Missing,
    Blocking,
}

/// <summary>
/// One line on the diagnosis page. The page exists to show what was actually found on the
/// machine, so each check carries its own detail rather than collapsing to a single boolean.
/// </summary>
public sealed record EnvironmentCheck(string Name, CheckStatus Status, string Detail)
{
    public string StatusText => Status switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Warning => "Warning",
        CheckStatus.Missing => "Not found",
        CheckStatus.Blocking => "Unsupported",
        _ => string.Empty,
    };
}

public sealed record DependencySelection(string Id, string Title, bool IsRequired)
{
    public bool IsConsented { get; set; }

    public bool IsInstalled { get; set; }

    /// <summary>
    /// False when no automated recipe exists. Such an item is shown as information rather
    /// than as a checkbox that would quietly do nothing.
    /// </summary>
    public bool IsInstallable { get; init; } = true;

    public string StateText => IsInstalled ? "Already installed" : "Not installed";

    /// <summary>
    /// What is given up by skipping this. "Optional" on its own invites skipping everything
    /// and finding the cost later, at the point where a tool quietly stops answering
    /// precisely — which is the worst moment to learn it.
    /// </summary>
    public string Consequence { get; init; } = string.Empty;

    /// <summary>
    /// Whether the wizard refuses to continue without it. Shown, because a list where
    /// every line looks equally mandatory makes an optional item feel like an obligation —
    /// and most of this list is exactly that: three to four gigabytes and several UAC
    /// prompts for capabilities a given user may not want.
    /// </summary>
    public string RequirementText => IsRequired
        ? "required"
        : Consequence.Length == 0 ? "optional" : "optional — " + Consequence;

    public string ActionText => !IsInstallable
        ? "Install manually"
        : IsInstalled ? "Reinstall" : "Install";
}

public sealed record RecommendedModel(string Id, string Purpose, string Detail);

public sealed record AgentOption(string Agent, AgentChoice Choice)
{
    public string DisplayName => Agent switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        _ => Agent,
    };

    public bool IsDetected { get; init; }

    public string DetectionText => IsDetected ? "detected" : "not detected";
}

/// <summary>
/// One entry in the orientation pane on the left of the wizard. It carries no page of its
/// own: the pane renders a title and a state, and both wizards — installation and removal —
/// have their own page enumerations to keep track of where they are.
/// </summary>
public sealed record WizardStep(string Title, bool IsCurrent, bool IsDone);

public static class InstallerCulture
{
    public static string CurrentCultureCode { get; set; } = CultureInfo.CurrentUICulture.Name;
}
