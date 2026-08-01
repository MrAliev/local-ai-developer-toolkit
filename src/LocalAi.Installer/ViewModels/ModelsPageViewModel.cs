using System.Collections.ObjectModel;

namespace LocalAi.Installer.ViewModels;

public sealed class ModelsPageViewModel : ObservableObject
{
    private ModelSelectionMode mode = ModelSelectionMode.Automatic;
    private string? manualModelId;
    private int manualContext;

    /// <summary>
    /// The models LocalAi actually routes to, matching the shipped routing catalog. This list
    /// used to name models the product does not use at all, which made the recommended option
    /// meaningless. When a release manifest carries a model catalogue, it replaces this.
    /// </summary>
    public ObservableCollection<RecommendedModel> RecommendedModels { get; } =
    [
        new("qwen3-embedding:8b-q8_0", "Code search embeddings",
            "Required for repository indexing. Roughly 7.5 GB."),
        new("qwen3.5:9b", "Text tasks",
            "Summaries and routine file work. Roughly 6 GB."),
        new("qwen2.5-coder:14b", "Code tasks",
            "Code reading and review. Roughly 8.5 GB."),
        new("qwen3-vl:8b-instruct-q8_0", "Images and OCR",
            "Screenshots and scanned documents. Roughly 9 GB."),
    ];

    public ModelSelectionMode Mode
    {
        get => mode;
        set
        {
            SetProperty(ref mode, value);
            OnPropertyChanged(nameof(IsAutomatic));
            OnPropertyChanged(nameof(IsManual));
            OnPropertyChanged(nameof(IsSkip));
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(ReviewText));
        }
    }

    public bool IsAutomatic
    {
        get => Mode == ModelSelectionMode.Automatic;
        set
        {
            if (value)
            {
                Mode = ModelSelectionMode.Automatic;
            }
        }
    }

    public bool IsManual
    {
        get => Mode == ModelSelectionMode.Manual;
        set
        {
            if (value)
            {
                Mode = ModelSelectionMode.Manual;
            }
        }
    }

    public bool IsSkip
    {
        get => Mode == ModelSelectionMode.Skip;
        set
        {
            if (value)
            {
                Mode = ModelSelectionMode.Skip;
            }
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
        ModelSelectionMode.Manual =>
            !string.IsNullOrWhiteSpace(ManualModelId) && ManualContextWindow > 0,
        _ => false,
    };

    public string ReviewText => Mode switch
    {
        ModelSelectionMode.Skip => "Models: skipped, nothing will be downloaded",
        ModelSelectionMode.Manual =>
            $"Models: {ManualModelId} with a {ManualContextWindow} token context",
        _ => "Models: " + string.Join(
            ", ",
            RecommendedModels.Select(model => model.Id)),
    };
}
