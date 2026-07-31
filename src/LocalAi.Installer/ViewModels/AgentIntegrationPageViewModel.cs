using System.Collections.ObjectModel;

namespace LocalAi.Installer.ViewModels;

public sealed class AgentIntegrationPageViewModel : ObservableObject
{
    public ObservableCollection<AgentOption> Agents { get; } =
    [
        new("codex", AgentChoice.None),
        new("claude", AgentChoice.None),
    ];

    public void SetChoice(string agent, AgentChoice choice)
    {
        for (var index = 0; index < Agents.Count; index++)
        {
            if (string.Equals(Agents[index].Agent, agent, StringComparison.OrdinalIgnoreCase))
            {
                Agents[index] = Agents[index] with { Choice = choice };
                OnPropertyChanged(nameof(Agents));
                OnPropertyChanged(nameof(CanContinue));
                OnPropertyChanged(nameof(ReviewText));
                return;
            }
        }

        throw new InvalidOperationException($"Unknown agent '{agent}'.");
    }

    public string ReviewText
    {
        get
        {
            return string.Join(
                "; ",
                Agents.Select(agent => $"{agent.Agent}:{agent.Choice}"));
        }
    }

    public bool CanContinue
    {
        get
        {
            return Agents.All(agent => agent.Choice != AgentChoice.None);
        }
    }
}
