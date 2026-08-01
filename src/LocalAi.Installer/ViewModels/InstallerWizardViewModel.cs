using System.Windows;

namespace LocalAi.Installer.ViewModels;

public sealed class InstallerWizardViewModel : ObservableObject
{
    private readonly DiagnosePageViewModel diagnose = new();
    private readonly DependenciesPageViewModel dependencies = new();
    private readonly PackagePageViewModel package = new();
    private readonly ModelsPageViewModel models = new();
    private readonly AgentIntegrationPageViewModel agents = new();
    private readonly ReviewApplyPageViewModel review = new();
    private readonly FinishPageViewModel finish = new();

    private InstallerPage currentPage = InstallerPage.Diagnose;
    private bool isCanceled;
    private bool isComplete;
    private int progress;
    private string progressText = "Ready";
    private string? rollbackMessage;
    private string language = InstallerCulture.CurrentCultureCode;

    public InstallerWizardViewModel()
    {
        diagnose.IsSupported = true;
        dependencies.MarkInstalled("Git");
    }

    public IReadOnlyList<string> PageNames { get; } =
    [
        "Diagnosis",
        "Dependencies",
        "Package",
        "Models",
        "Agents",
        "Review",
        "Apply",
    ];

    public InstallerPage CurrentPage
    {
        get => currentPage;
        private set
        {
            if (currentPage == value)
            {
                return;
            }

            currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(StepDescription));
            OnPropertyChanged(nameof(StepStatus));
            OnPropertyChanged(nameof(CanMovePrevious));
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(RunButtonVisibility));
        }
    }

    public DiagnosePageViewModel Diagnose => diagnose;
    public DependenciesPageViewModel Dependencies => dependencies;
    public PackagePageViewModel Package => package;
    public ModelsPageViewModel Models => models;
    public AgentIntegrationPageViewModel Agents => agents;
    public ReviewApplyPageViewModel Review => review;
    public FinishPageViewModel Finish => finish;

    public string StepTitle => PageNames[(int)CurrentPage];

    public string StepDescription => CurrentPage switch
    {
        InstallerPage.Diagnose => "Run environment and compatibility checks.",
        InstallerPage.Dependencies => "Select optional dependency installation and consent.",
        InstallerPage.Package => "Choose a target package and confirm compatibility.",
        InstallerPage.Models => "Pick model strategy: automatic or manual.",
        InstallerPage.Agents => "Choose per-agent integration options.",
        InstallerPage.ReviewApply => "Review planned actions before applying.",
        _ => "Installation finished.",
    };

    public string StepStatus => $"Step {(int)CurrentPage + 1} of {PageNames.Count}";

    public bool CanMovePrevious => CurrentPage > InstallerPage.Diagnose && !isCanceled;

    public bool CanMoveNext => CurrentPage switch
    {
        InstallerPage.Diagnose => !isCanceled && diagnose.CanContinue,
        InstallerPage.Dependencies => !isCanceled && dependencies.CanContinue,
        InstallerPage.Package => !isCanceled && package.CanContinue,
        InstallerPage.Models => !isCanceled && models.CanContinue,
        InstallerPage.Agents => !isCanceled && agents.CanContinue,
        InstallerPage.ReviewApply => !isCanceled && review.CanApply,
        _ => false,
    };

    public bool CanRun => CurrentPage == InstallerPage.ReviewApply && !isCanceled && review.CanApply;

    public Visibility RunButtonVisibility =>
        CanRun ? Visibility.Visible : Visibility.Collapsed;

    public bool IsCanceled => isCanceled;

    public bool IsComplete => isComplete;

    public int Progress => progress;

    public string ProgressText => progressText;

    public string? ReviewText => review.Render(diagnose, dependencies, models, agents);

    public string Language
    {
        get => language;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                value = "en-US";
            }

            SetProperty(ref language, value);
            InstallerCulture.CurrentCultureCode = value;
            OnPropertyChanged(nameof(IsRussian));
        }
    }

    public bool IsRussian => string.Equals(Language, "ru-RU", StringComparison.Ordinal);

    public bool RequiresRestart => finish.RequiresRestart;

    public string? RollbackResult => rollbackMessage;

    public int CurrentPageIndex => (int)CurrentPage;

    public bool MoveNext()
    {
        if (!CanMoveNext)
        {
            return false;
        }

        if (CurrentPage is InstallerPage.ReviewApply)
        {
            return false;
        }

        CurrentPage = CurrentPage switch
        {
            InstallerPage.Diagnose => InstallerPage.Dependencies,
            InstallerPage.Dependencies => InstallerPage.Package,
            InstallerPage.Package => InstallerPage.Models,
            InstallerPage.Models => InstallerPage.Agents,
            InstallerPage.Agents => InstallerPage.ReviewApply,
            InstallerPage.Finish => InstallerPage.Finish,
            _ => CurrentPage,
        };
        return true;
    }

    public bool MovePrevious()
    {
        if (!CanMovePrevious || isCanceled)
        {
            return false;
        }

        CurrentPage = CurrentPage switch
        {
            InstallerPage.Dependencies => InstallerPage.Diagnose,
            InstallerPage.Package => InstallerPage.Dependencies,
            InstallerPage.Models => InstallerPage.Package,
            InstallerPage.Agents => InstallerPage.Models,
            InstallerPage.ReviewApply => InstallerPage.Agents,
            InstallerPage.Finish => InstallerPage.ReviewApply,
            _ => InstallerPage.Diagnose,
        };
        return true;
    }

    public bool Run()
    {
        if (!CanRun || isCanceled)
        {
            return false;
        }

        CurrentPage = InstallerPage.Finish;
        progress = 100;
        progressText = "Completed";
        isComplete = true;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(RequiresRestart));
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunButtonVisibility));
        return true;
    }

    public void Cancel()
    {
        isCanceled = true;
        isComplete = false;
        OnPropertyChanged(nameof(IsCanceled));
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(CanRun));
    }

    public void SetProgress(int value, string message)
    {
        progress = Math.Clamp(value, 0, 100);
        progressText = message;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    public void SetRollbackInfo(string message, bool requiresRestart)
    {
        rollbackMessage = message;
        finish.RequiresRestart = requiresRestart;
        finish.RollbackNotes = message;
        OnPropertyChanged(nameof(RollbackResult));
        OnPropertyChanged(nameof(RequiresRestart));
    }

    public void ConfirmReview()
    {
        review.IsConfirmed = true;
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunButtonVisibility));
    }
}
