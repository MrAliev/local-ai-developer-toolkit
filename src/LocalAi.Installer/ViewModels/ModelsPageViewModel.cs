using System.Collections.ObjectModel;
using LocalAi.Contracts;

namespace LocalAi.Installer.ViewModels;

public sealed class ModelsPageViewModel : ObservableObject
{
    private ModelSelectionMode mode = ModelSelectionMode.Automatic;
    private ModelCatalogEntry? selectedModel;
    private int selectedContext;

    public ModelsPageViewModel()
    {
        foreach (var model in ModelRoutingCatalogResource.SelectableModels())
        {
            CatalogModels.Add(model);
        }

        SelectedModel = CatalogModels.FirstOrDefault();
    }

    /// <summary>
    /// Everything the broker knows how to route. A model outside this list cannot be loaded
    /// at all — the routing catalog is what supplies its capabilities and permitted context
    /// sizes, and the model registry does not publish either. So the page offers a choice
    /// from this list rather than a free-text box that would accept a guaranteed failure.
    /// </summary>
    public ObservableCollection<ModelCatalogEntry> CatalogModels { get; } = [];

    public ObservableCollection<int> AvailableContexts { get; } = [];

    public ModelSelectionMode Mode
    {
        get => mode;
        set
        {
            SetProperty(ref mode, value);
            OnPropertyChanged(nameof(IsAutomatic));
            OnPropertyChanged(nameof(IsChooseExact));
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

    public bool IsChooseExact
    {
        get => Mode == ModelSelectionMode.ChooseExact;
        set
        {
            if (value)
            {
                Mode = ModelSelectionMode.ChooseExact;
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

    public ModelCatalogEntry? SelectedModel
    {
        get => selectedModel;
        set
        {
            SetProperty(ref selectedModel, value);
            RebuildContexts();
            OnPropertyChanged(nameof(SelectedModelPurpose));
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(ReviewText));
        }
    }

    /// <summary>
    /// Restricted to the context sizes the selected model actually declares. Choosing a
    /// smaller context is the practical way to fit a model on a machine with little video
    /// memory, so it is offered rather than hidden.
    /// </summary>
    public int SelectedContext
    {
        get => selectedContext;
        set
        {
            SetProperty(ref selectedContext, value);
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(ReviewText));
        }
    }

    public string SelectedModelPurpose => selectedModel is null
        ? string.Empty
        : string.Join(", ", selectedModel.Capabilities);

    public bool CanContinue => Mode switch
    {
        ModelSelectionMode.Skip => true,
        ModelSelectionMode.Automatic => CatalogModels.Count > 0,
        ModelSelectionMode.ChooseExact =>
            selectedModel is not null &&
            selectedContext > 0 &&
            selectedModel.ContextTokens.Contains(selectedContext),
        _ => false,
    };

    public string ReviewText => Mode switch
    {
        ModelSelectionMode.Skip => "Models: skipped, nothing will be downloaded",
        ModelSelectionMode.ChooseExact =>
            $"Models: {selectedModel?.Tag} with a {selectedContext} token context",
        _ => "Models: chosen automatically from " +
            $"{CatalogModels.Count} catalogue entries that fit this machine",
    };

    private void RebuildContexts()
    {
        AvailableContexts.Clear();
        if (selectedModel is null)
        {
            selectedContext = 0;
            OnPropertyChanged(nameof(SelectedContext));
            return;
        }

        foreach (var context in selectedModel.ContextTokens.OrderBy(value => value))
        {
            AvailableContexts.Add(context);
        }

        // Keep the current context when the new model also permits it, so switching models
        // does not silently change a deliberate choice.
        if (!AvailableContexts.Contains(selectedContext))
        {
            selectedContext = AvailableContexts.FirstOrDefault();
        }

        OnPropertyChanged(nameof(AvailableContexts));
        OnPropertyChanged(nameof(SelectedContext));
    }
}
