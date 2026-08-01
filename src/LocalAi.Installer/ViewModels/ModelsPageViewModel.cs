using System.Collections.ObjectModel;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.ViewModels;

public sealed class ModelsPageViewModel : ObservableObject
{
    private ModelSelectionMode mode = ModelSelectionMode.Automatic;
    private ModelCatalogEntry? selectedModel;
    private int selectedContext;
    private CatalogRecommendation recommendation = CatalogRecommendation.Empty;
    private bool residencyRequiresVideoMemory = true;

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

    public ObservableCollection<string> AutomaticSelection { get; } = [];

    public string AutomaticSummary { get; private set; } =
        "Model sizes have not been checked yet.";

    /// <summary>
    /// Applies a computed recommendation.
    ///
    /// <paramref name="residencyRequiresVideoMemory"/> comes from the residency page: the
    /// engine weighs models against dedicated video memory, which is the right question only
    /// while the strict policy is in force. Once the user has accepted running from system
    /// memory, refusing a model for not fitting the adapter would contradict the choice they
    /// just made, so the fit is reported as information rather than as a filter.
    /// </summary>
    public void ApplyRecommendation(
        CatalogRecommendation value,
        bool residencyRequiresVideoMemory)
    {
        recommendation = value ?? CatalogRecommendation.Empty;
        this.residencyRequiresVideoMemory = residencyRequiresVideoMemory;

        AutomaticSelection.Clear();
        if (!recommendation.SizesKnown)
        {
            AutomaticSummary = recommendation.AdapterExplanation +
                " Every catalogue model will be offered.";
            foreach (var model in CatalogModels)
            {
                AutomaticSelection.Add($"{model.Tag} — size unknown");
            }
        }
        else
        {
            var usable = residencyRequiresVideoMemory
                ? recommendation.Fitting
                : recommendation.Fits;
            foreach (var fit in usable
                         .GroupBy(fit => fit.Tag, StringComparer.Ordinal)
                         .Select(group => group.OrderByDescending(fit => fit.ContextTokens).First()))
            {
                AutomaticSelection.Add(
                    $"{fit.Tag} — {fit.DownloadSizeBytes / (1024d * 1024 * 1024):N1} GB, " +
                    $"context {fit.ContextTokens}");
            }

            AutomaticSummary = AutomaticSelection.Count > 0
                ? recommendation.AdapterExplanation
                : "No catalogue model fits the detected video memory. Relax the video memory " +
                    "setting on the previous page to use this computer anyway, or skip model " +
                    "setup for now.";
        }

        OnPropertyChanged(nameof(AutomaticSelection));
        OnPropertyChanged(nameof(AutomaticSummary));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ReviewText));
    }

    public bool CanContinue => Mode switch
    {
        ModelSelectionMode.Skip => true,
        // Automatic is a valid answer even when nothing fits: the page says so, and Skip
        // remains available. Blocking here would trap the user with no way forward.
        ModelSelectionMode.Automatic => true,
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
        _ => AutomaticSelection.Count > 0
            ? "Models: " + string.Join(", ", AutomaticSelection)
            : "Models: none fit this computer with the current video memory setting",
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
