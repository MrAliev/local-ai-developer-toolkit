using System.Collections.ObjectModel;

namespace LocalAi.Installer.ViewModels;

public sealed class DependenciesPageViewModel : ObservableObject
{
    public ObservableCollection<DependencySelection> Dependencies { get; } =
    [
        new("Git", "Git", true),
        new("VisualCpp", "MSVC redistributable", false),
        new("Ollama", "Ollama CLI", false),
    ];

    public IReadOnlyList<DependencySelection> SelectedDependencies =>
        [.. Dependencies];

    public bool CanContinue
    {
        get
        {
            return !Dependencies.Any(dependency => dependency.IsRequired && !dependency.IsInstalled && !dependency.IsConsented);
        }
    }

    public void SetConsent(string id, bool consent)
    {
        var dependency = Dependencies.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (dependency is null)
        {
            throw new InvalidOperationException($"Unknown dependency '{id}'.");
        }

        dependency.IsConsented = consent;
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(Dependencies));
    }

    public void SetInstalled(string id, bool installed)
    {
        var dependency = Dependencies.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (dependency is null)
        {
            throw new InvalidOperationException($"Unknown dependency '{id}'.");
        }

        dependency.IsInstalled = installed;
        if (installed)
        {
            dependency.IsConsented = true;
        }

        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(Dependencies));
    }

    public void MarkInstalled(string id)
    {
        var dependency = Dependencies.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (dependency is null)
        {
            throw new InvalidOperationException($"Unknown dependency '{id}'.");
        }

        dependency.IsInstalled = true;
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(Dependencies));
    }
}
