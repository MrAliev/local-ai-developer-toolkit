using System.Collections.ObjectModel;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Per-client integration choice. Defaults to leaving a client untouched, so nothing is
/// reconfigured without an explicit decision.
/// </summary>
public sealed class AgentIntegrationPageViewModel : ObservableObject
{
    public ObservableCollection<AgentOption> Agents { get; } =
    [
        new("codex", AgentChoice.NoChange),
        new("claude", AgentChoice.NoChange),
    ];

    public IReadOnlyList<AgentChoice> Choices { get; } =
    [
        AgentChoice.McpAndInstructions,
        AgentChoice.McpOnly,
        AgentChoice.InstructionsOnly,
        AgentChoice.NoChange,
    ];

    /// <summary>
    /// Every option, including "leave unchanged", is a valid answer, so there is no state in
    /// which the user has failed to decide and this page never blocks navigation.
    /// </summary>
    public bool CanContinue => true;

    public string ReviewText =>
        "Clients: " + string.Join(
            "; ",
            Agents.Select(agent => $"{agent.DisplayName} — {agent.Choice.Title()}"));

    public void SetChoice(string agent, AgentChoice choice)
    {
        for (var index = 0; index < Agents.Count; index++)
        {
            if (!string.Equals(Agents[index].Agent, agent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Agents[index] = Agents[index] with { Choice = choice };
            OnPropertyChanged(nameof(Agents));
            OnPropertyChanged(nameof(ReviewText));
            return;
        }

        throw new InvalidOperationException($"Unknown agent '{agent}'.");
    }

    /// <summary>
    /// Records which clients were actually found, so the page can say so instead of silently
    /// offering to configure something that is not installed.
    /// </summary>
    public void ApplyDetection(IReadOnlyList<AgentSnapshot> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        for (var index = 0; index < Agents.Count; index++)
        {
            var kind = string.Equals(Agents[index].Agent, "claude", StringComparison.Ordinal)
                ? AgentKind.Claude
                : AgentKind.Codex;
            var detected = agents.Any(agent =>
                agent.Kind == kind &&
                (agent.Executable.Exists || agent.Config.Exists));
            Agents[index] = Agents[index] with { IsDetected = detected };
        }

        OnPropertyChanged(nameof(Agents));
    }
}
