using System.Collections.ObjectModel;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Per-client integration choice.
///
/// A detected client defaults to full integration: registering the MCP servers and writing
/// the managed instructions is the entire reason the rest of the installation is worth
/// anything, and defaulting to "leave unchanged" produced installations where the binaries
/// were present and no client could reach them. Nothing is applied silently — the review
/// page lists the choice and the run only starts on explicit confirmation.
///
/// A client that was not detected stays untouched, and an explicit choice always wins over
/// the default, including a deliberate "leave unchanged".
/// </summary>
public sealed class AgentIntegrationPageViewModel : ObservableObject
{
    private readonly HashSet<string> explicitChoices =
        new(StringComparer.OrdinalIgnoreCase);

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
    /// The same four choices with something to display. The combo box was bound straight to the
    /// enum values and had no display projection, so the page where the choice is made read
    /// "McpAndInstructions" and "NoChange" — the titles existed, and only the review page ever
    /// used them.
    /// </summary>
    public IReadOnlyList<AgentChoiceOption> ChoiceOptions { get; }

    public AgentIntegrationPageViewModel() =>
        ChoiceOptions = [.. Choices.Select(choice => new AgentChoiceOption(choice))];

    /// <summary>
    /// Every option, including "leave unchanged", is a valid answer, so there is no state in
    /// which the user has failed to decide and this page never blocks navigation.
    /// </summary>
    public bool CanContinue => true;

    public string ReviewText =>
        InstallerCulture.Pick("Clients: ", "Клиенты: ") + string.Join(
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

            explicitChoices.Add(Agents[index].Agent);
            Agents[index] = Agents[index] with { Choice = choice };
            OnPropertyChanged(nameof(Agents));
            OnPropertyChanged(nameof(ReviewText));
            return;
        }

        throw new InvalidOperationException($"Unknown agent '{agent}'.");
    }

    /// <summary>
    /// Records which clients were actually found, so the page can say so instead of silently
    /// offering to configure something that is not installed, and arms the default: a client
    /// that exists is set up unless the user says otherwise.
    ///
    /// Detection runs more than once during a session, so a client that disappears between
    /// runs must lose the default it was given; only a choice the user made by hand is kept
    /// across refreshes.
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
            var choice = explicitChoices.Contains(Agents[index].Agent)
                ? Agents[index].Choice
                : detected
                    ? AgentChoice.McpAndInstructions
                    : AgentChoice.NoChange;
            Agents[index] = Agents[index] with { IsDetected = detected, Choice = choice };
        }

        OnPropertyChanged(nameof(Agents));
        OnPropertyChanged(nameof(ReviewText));
    }
}
