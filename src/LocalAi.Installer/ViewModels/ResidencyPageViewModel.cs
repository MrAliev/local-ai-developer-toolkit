using LocalAi.Contracts;
using LocalAi.Installer.Core;

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

    /// <summary>
    /// Which card the rule is about, in the words the machine reports it.
    ///
    /// Set from the adapter the recommendation weighed against — the one with the most
    /// dedicated video memory — so the group states its subject rather than leaving the
    /// reader to infer it from a number further down.
    /// </summary>
    public string AdapterFound
    {
        get => adapterFound;
        set
        {
            SetProperty(ref adapterFound, value);
            OnPropertyChanged(nameof(HasAdapterFound));
        }
    }

    public bool HasAdapterFound => !string.IsNullOrWhiteSpace(AdapterFound);

    private string adapterFound = string.Empty;

    public string AdapterHint => HasUsableAdapter
        ? string.Empty
        : InstallerCulture.Pick(
            "No adapter with dedicated video memory was found. With the strict setting no " +
            "model will load on this machine; pick one of the relaxed options to use it " +
            "anyway, and expect a large slowdown.",
            "Адаптер с выделенной видеопамятью не найден. При строгом правиле " +
            "на этом компьютере не загрузится ни одна модель; выберите одно из " +
            "смягчённых правил, чтобы всё же им пользоваться, и ожидайте " +
            "сильного замедления.");

    public bool HasWarning => Policy != ModelResidencyPolicy.RequireFullVram;

    public string Warning => Policy switch
    {
        ModelResidencyPolicy.AllowPartialOffload => InstallerCulture.Pick(
            "Part of a model may spill into system memory. Responses will be slower, and " +
            "every answer produced that way is labelled as degraded.",
            "Часть модели может уйти в системную память. Ответы станут " +
            "медленнее, и каждый полученный так ответ помечается как ухудшенный."),
        ModelResidencyPolicy.AllowCpu => InstallerCulture.Pick(
            "Models may run entirely on the CPU. Expect a large slowdown, and every answer " +
            "produced that way is labelled as degraded.",
            "Модели могут работать целиком на процессоре. Ожидайте сильного " +
            "замедления; каждый полученный так ответ помечается как ухудшенный."),
        _ => string.Empty,
    };

    public bool CanContinue => true;

    /// <summary>
    /// The rule in the words the page used to offer it, not the name the enum happens to
    /// carry. "Model residency: RequireFullVram" put an identifier into the list somebody
    /// reads before consenting — and StoredResidencyNote then printed a second one beside it.
    /// </summary>
    public string ReviewText =>
        InstallerCulture.Pick("Video memory: ", "Видеопамять: ") + Name(Policy);

    public static string Name(ModelResidencyPolicy policy) => policy switch
    {
        ModelResidencyPolicy.RequireFullVram => InstallerCulture.Pick(
            "whole model in video memory",
            "вся модель в видеопамяти"),
        ModelResidencyPolicy.AllowPartialOffload => InstallerCulture.Pick(
            "part of the model in system memory",
            "часть модели в системной памяти"),
        ModelResidencyPolicy.AllowCpu => InstallerCulture.Pick(
            "running on the processor",
            "работа на процессоре"),
        _ => policy.ToString(),
    };
}
