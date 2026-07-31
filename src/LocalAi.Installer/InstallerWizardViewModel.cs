using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace LocalAi.Installer;

public sealed class InstallerWizardViewModel : INotifyPropertyChanged
{
    private readonly string[] stepTitles =
    [
        "Review",
        "Dependencies",
        "Model",
        "Agents",
        "Run",
    ];

    private readonly string[] stepDescriptions =
    [
        "Review the detected environment and supported installer capabilities.",
        "Select optional dependency actions and confirm consent.",
        "Review recommended local models and broker preflight criteria.",
        "Review agent configuration mode and persistence options.",
        "Run installer steps and observe resumable execution outcomes.",
    ];

    private int stepIndex;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int StepCount => stepTitles.Length;

    public int StepIndex
    {
        get => stepIndex;
        private set
        {
            if (stepIndex == value)
            {
                return;
            }

            stepIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(StepDescription));
            OnPropertyChanged(nameof(CanMovePrevious));
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(RunButtonVisibility));
        }
    }

    public string StepTitle => stepTitles[Math.Clamp(StepIndex, 0, StepCount - 1)];

    public string StepDescription => stepDescriptions[Math.Clamp(StepIndex, 0, StepCount - 1)];

    public string StepStatus => $"Step {Math.Clamp(StepIndex, 0, StepCount - 1) + 1} of {StepCount}";

    public bool CanMovePrevious => StepIndex > 0;

    public bool CanMoveNext => StepIndex < StepCount - 2;

    public bool CanRun => StepIndex == StepCount - 2;

    public Visibility RunButtonVisibility => CanRun ? Visibility.Visible : Visibility.Collapsed;

    public void MoveNext()
    {
        if (CanMoveNext)
        {
            StepIndex++;
        }
    }

    public void MovePrevious()
    {
        if (CanMovePrevious)
        {
            StepIndex--;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
