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
            AutomaticSummary =
                "Model sizes could not be retrieved, so nothing was weighed against this " +
                "computer. Every catalogue model will be offered.";
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

            // The rule is now above this list on the same page, so "the previous page" would
            // name a route that no longer exists (#257).
            AutomaticSummary = AutomaticSelection.Count > 0
                ? string.Empty
                : "No catalogue model fits the detected video memory. Pick a relaxed rule " +
                    "above to use this computer anyway, or skip model setup for now.";
        }

        RuleSummary = BuildRuleSummary();
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

    /// <summary>
    /// The page's answer in the form the installation understands.
    ///
    /// Deliberately not a list of tags: the sizes shown here come from the routing catalogue
    /// and the model registry, neither of which is signed, and the broker installer only
    /// accepts models weighed against the release manifest. So the page states the intent and
    /// the signed manifest decides the set.
    /// </summary>
    public ModelProvisioningSelection BuildProvisioningSelection() => Mode switch
    {
        ModelSelectionMode.Skip => ModelProvisioningSelection.None,
        ModelSelectionMode.ChooseExact when selectedModel is not null && selectedContext > 0 =>
            new(ModelProvisioningMode.Exact, selectedModel.Tag, selectedContext),
        ModelSelectionMode.ChooseExact => ModelProvisioningSelection.None,
        _ => new(ModelProvisioningMode.Automatic),
    };

    /// <summary>
    /// The rule this list follows, restated inside the group the rule governs.
    ///
    /// The two used to be separate pages, so the recompute a rule change triggers landed
    /// where nobody could see it. On one page the count moves the moment the rule does, and
    /// this line keeps the dependency legible once the radio buttons have scrolled away.
    /// </summary>
    public string RuleSummary
    {
        get => ruleSummary;
        private set => SetProperty(ref ruleSummary, value);
    }

    private string ruleSummary = "Weighing models against this computer.";

    /// <summary>
    /// "Whole model in video memory - 4 of 6 catalogue models fit", or its relaxed form.
    ///
    /// Named for the rule rather than for the policy enum, and counted against the catalogue
    /// rather than against the fits list, so the number answers "how much of what is on offer
    /// can this computer take".
    /// </summary>
    private string BuildRuleSummary()
    {
        var rule = residencyRequiresVideoMemory
            ? "Whole model in video memory"
            : "Relaxed video memory rule";
        if (!recommendation.SizesKnown)
        {
            return rule + " - model sizes are unknown, so nothing was weighed";
        }

        var catalogue = CatalogModels.Count;
        if (!residencyRequiresVideoMemory)
        {
            return catalogue == 0
                ? rule
                : $"{rule} - all {catalogue} catalogue models offered";
        }

        var fitting = recommendation.Fitting
            .Select(fit => fit.Tag)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return $"{rule} - {fitting} of {catalogue} catalogue models fit";
    }

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
