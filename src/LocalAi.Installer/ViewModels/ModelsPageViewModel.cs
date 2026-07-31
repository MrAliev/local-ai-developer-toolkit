using System.Collections.ObjectModel;

namespace LocalAi.Installer.ViewModels;

public sealed class ModelsPageViewModel : ObservableObject
{
    private ModelSelectionMode mode = ModelSelectionMode.Automatic;
    private string? manualModelId;
    private int manualContext;

    public ObservableCollection<RecommendedModel> RecommendedModels { get; } =
    [
        new("llama3.2:8b", "low"),
        new("qwen2.5:14b", "medium"),
        new("phi4:14b", "high"),
    ];

    public ModelSelectionMode Mode
    {
        get => mode;
        set
        {
            SetProperty(ref mode, value);
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(ReviewText));
        }
    }

    public string? ManualModelId
    {
        get => manualModelId;
        set
        {
            SetProperty(ref manualModelId, value);
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(ReviewText));
        }
    }

    public int ManualContextWindow
    {
        get => manualContext;
        set
        {
            SetProperty(ref manualContext, value);
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(ReviewText));
        }
    }

    public bool CanContinue => Mode switch
    {
        ModelSelectionMode.Skip => true,
        ModelSelectionMode.Automatic => RecommendedModels.Count > 0,
        ModelSelectionMode.Manual => !string.IsNullOrWhiteSpace(ManualModelId) && ManualContextWindow > 0,
        _ => false,
    };

    public string ReviewText
    {
        get
        {
            return Mode switch
            {
                ModelSelectionMode.Skip => "Models: skipped",
                ModelSelectionMode.Manual => $"Models: manual {ManualModelId} ctx={ManualContextWindow}",
                _ => $"Models: automatic ({RecommendedModels.Count})",
            };
        }
    }
}
