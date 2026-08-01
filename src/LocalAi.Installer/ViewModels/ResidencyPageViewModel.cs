using LocalAi.Contracts;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Chooses how strictly models must fit in video memory.
///
/// This is the page that decides whether the product is usable at all on a machine without a
/// discrete adapter, so it states the cost of each option rather than presenting them as
/// equivalent. The default stays strict even when no adapter was found: silently relaxing a
/// safety check on the user's behalf is exactly what this page is meant to avoid.
/// </summary>
public sealed class ResidencyPageViewModel : ObservableObject
{
    private ModelResidencyPolicy policy = ModelResidencyPolicy.RequireFullVram;
    private bool hasUsableAdapter = true;

    public ModelResidencyPolicy Policy
    {
        get => policy;
        set
        {
            SetProperty(ref policy, value);
            OnPropertyChanged(nameof(IsRequireFullVram));
            OnPropertyChanged(nameof(IsAllowPartialOffload));
            OnPropertyChanged(nameof(IsAllowCpu));
            OnPropertyChanged(nameof(Warning));
            OnPropertyChanged(nameof(HasWarning));
            OnPropertyChanged(nameof(ReviewText));
        }
    }

    public bool IsRequireFullVram
    {
        get => Policy == ModelResidencyPolicy.RequireFullVram;
        set
        {
            if (value)
            {
                Policy = ModelResidencyPolicy.RequireFullVram;
            }
        }
    }

    public bool IsAllowPartialOffload
    {
        get => Policy == ModelResidencyPolicy.AllowPartialOffload;
        set
        {
            if (value)
            {
                Policy = ModelResidencyPolicy.AllowPartialOffload;
            }
        }
    }

    public bool IsAllowCpu
    {
        get => Policy == ModelResidencyPolicy.AllowCpu;
        set
        {
            if (value)
            {
                Policy = ModelResidencyPolicy.AllowCpu;
            }
        }
    }

    /// <summary>
    /// Set from the diagnosis so the page can point out that the strict default will refuse
    /// to load anything on this machine — without changing the choice by itself.
    /// </summary>
    public bool HasUsableAdapter
    {
        get => hasUsableAdapter;
        set
        {
            SetProperty(ref hasUsableAdapter, value);
            OnPropertyChanged(nameof(AdapterHint));
            OnPropertyChanged(nameof(HasAdapterHint));
        }
    }

    public bool HasAdapterHint => !HasUsableAdapter;

    public string AdapterHint => HasUsableAdapter
        ? string.Empty
        : "No adapter with dedicated video memory was found. With the strict setting no " +
            "model will load on this machine; pick one of the relaxed options to use it " +
            "anyway, and expect a large slowdown.";

    public bool HasWarning => Policy != ModelResidencyPolicy.RequireFullVram;

    public string Warning => Policy switch
    {
        ModelResidencyPolicy.AllowPartialOffload =>
            "Part of a model may spill into system memory. Responses will be slower, and each " +
            "degraded answer will say so.",
        ModelResidencyPolicy.AllowCpu =>
            "Models may run entirely on the CPU. Expect a large slowdown. Each degraded " +
            "answer will say so.",
        _ => string.Empty,
    };

    public bool CanContinue => true;

    public string ReviewText => $"Model residency: {Policy}";
}
