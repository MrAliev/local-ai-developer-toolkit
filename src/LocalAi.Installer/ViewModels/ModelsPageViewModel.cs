using System.Collections.ObjectModel;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core;

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

    public string AutomaticSummary { get; private set; } = InstallerCulture.Pick(
        "Model sizes have not been checked yet.",
        "Размеры моделей ещё не проверялись.");

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
            AutomaticSummary = InstallerCulture.Pick(
                "Model sizes could not be retrieved, so nothing was weighed against this " +
                "computer. Every catalogue model will be offered.",
                "Размеры моделей получить не удалось, поэтому под этот " +
                "компьютер ничего не взвешивалось. Будут предложены все " +
                "модели каталога.");
            foreach (var model in CatalogModels)
            {
                AutomaticSelection.Add(string.Format(
                    InstallerCulture.Pick("{0} — size unknown", "{0} — размер неизвестен"),
                    model.Tag));
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
                AutomaticSelection.Add(string.Format(
                    InstallerCulture.Pick(
                        "{0} — {1:N1} GB, context {2}",
                        "{0} — {1:N1} ГБ, контекст {2}"),
                    fit.Tag,
                    fit.DownloadSizeBytes / (1024d * 1024 * 1024),
                    fit.ContextTokens));
            }

            // The rule is now above this list on the same page, so "the previous page" would
            // name a route that no longer exists (#257).
            AutomaticSummary = AutomaticSelection.Count > 0
                ? string.Empty
                : InstallerCulture.Pick(
                    "No catalogue model fits the detected video memory. Pick a relaxed rule " +
                    "above to use this computer anyway, or skip model setup for now.",
                    "Ни одна модель каталога не помещается в обнаруженную " +
                    "видеопамять. Выберите смягчённое правило выше, чтобы всё же " +
                    "пользоваться этим компьютером, или пока пропустите " +
                    "настройку моделей.");
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

    private string ruleSummary = InstallerCulture.Pick(
        "Weighing models against this computer.",
        "Взвешиваю модели под этот компьютер.");

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
            ? InstallerCulture.Pick("Whole model in video memory", "Вся модель в видеопамяти")
            : InstallerCulture.Pick("Relaxed video memory rule", "Смягчённое правило видеопамяти");
        if (!recommendation.SizesKnown)
        {
            return string.Format(
                InstallerCulture.Pick(
                    "{0} — model sizes are unknown, so nothing was weighed",
                    "{0} — размеры моделей неизвестны, взвешивать нечего"),
                rule);
        }

        var catalogue = CatalogModels.Count;
        if (!residencyRequiresVideoMemory)
        {
            return catalogue == 0
                ? rule
                : string.Format(
                    InstallerCulture.Pick(
                        "{0} — all {1} catalogue models offered",
                        "{0} — предложены все модели каталога ({1})"),
                    rule,
                    catalogue);
        }

        var fitting = recommendation.Fitting
            .Select(fit => fit.Tag)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return string.Format(
            InstallerCulture.Pick(
                "{0} — {1} of {2} catalogue models fit",
                "{0} — подходящих моделей: {1} из {2}"),
            rule,
            fitting,
            catalogue);
    }

    public string ReviewText => Mode switch
    {
        ModelSelectionMode.Skip => InstallerCulture.Pick(
            "Models: skipped, nothing will be downloaded",
            "Модели: пропущены, ничего не будет скачано"),
        ModelSelectionMode.ChooseExact => string.Format(
            InstallerCulture.Pick(
                "Models: {0} with a {1} token context",
                "Модели: {0}, контекст {1} токенов"),
            selectedModel?.Tag,
            selectedContext),
        _ => AutomaticSelection.Count > 0
            ? InstallerCulture.Pick("Models: ", "Модели: ") +
                string.Join(", ", AutomaticSelection)
            : InstallerCulture.Pick(
                "Models: none fit this computer with the current video memory setting",
                "Модели: при текущей настройке видеопамяти ни одна не " +
                "подходит этому компьютеру"),
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
