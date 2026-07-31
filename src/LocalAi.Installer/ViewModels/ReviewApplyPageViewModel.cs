namespace LocalAi.Installer.ViewModels;

public sealed class ReviewApplyPageViewModel : ObservableObject
{
    private bool isConfirmed;

    public bool IsConfirmed
    {
        get => isConfirmed;
        set
        {
            SetProperty(ref isConfirmed, value);
            OnPropertyChanged(nameof(CanApply));
        }
    }

    public bool CanApply =>
        IsConfirmed;

    public string Render(
        DiagnosePageViewModel diagnose,
        DependenciesPageViewModel dependencies,
        ModelsPageViewModel models,
        AgentIntegrationPageViewModel agents)
    {
        var lines = new[]
        {
            $"OS supported: {diagnose.IsSupported}",
            $"Dependencies: {dependencies.Dependencies.Count(d => d.IsInstalled || d.IsConsented)}/{dependencies.Dependencies.Count}",
            models.ReviewText,
            $"Agents: {agents.ReviewText}",
        };

        return string.Join(Environment.NewLine, lines);
    }
}
