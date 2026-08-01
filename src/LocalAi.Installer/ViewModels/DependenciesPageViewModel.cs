using System.Collections.ObjectModel;

namespace LocalAi.Installer.ViewModels;

public sealed class DependenciesPageViewModel : ObservableObject
{
    /// <summary>
    /// Only items the installer can actually act on are offered as choices. The MSVC
    /// redistributable has no automated recipe, so it is listed as information instead of a
    /// checkbox that silently does nothing.
    /// </summary>
    public ObservableCollection<DependencySelection> Dependencies { get; } =
    [
        new("Git", "Git", true),
        new("Ollama", "Ollama", true),
        // Required because the release repository is private: the installer reads it with
        // the sign-in already established by 'gh auth login' rather than handling a token.
        new("GitHubCli", "GitHub CLI", true),
        new("VisualCpp", "MSVC redistributable", false) { IsInstallable = false },
    ];

    public IReadOnlyList<DependencySelection> SelectedDependencies =>
        [.. Dependencies.Where(dependency => dependency.IsConsented)];

    /// <summary>
    /// A required dependency blocks the wizard only while it is neither present nor selected
    /// for installation. Nothing is pre-selected: consent is the user's to give.
    /// </summary>
    public bool CanContinue =>
        !Dependencies.Any(dependency =>
            dependency.IsRequired &&
            dependency.IsInstallable &&
            !dependency.IsInstalled &&
            !dependency.IsConsented);

    public string ReviewText
    {
        get
        {
            var selected = Dependencies
                .Where(dependency => dependency.IsConsented)
                .Select(dependency =>
                    $"{dependency.Title} ({(dependency.IsInstalled ? "reinstall" : "install")})")
                .ToArray();
            return selected.Length == 0
                ? "Dependencies: nothing selected"
                : "Dependencies: " + string.Join(", ", selected);
        }
    }

    public void SetConsent(string id, bool consent)
    {
        var dependency = Find(id);
        if (!dependency.IsInstallable && consent)
        {
            return;
        }

        dependency.IsConsented = consent;
        Notify();
    }

    /// <summary>
    /// Records what the environment probe found. Detection deliberately does not grant
    /// consent: an already installed dependency must not be reinstalled unless asked.
    /// </summary>
    public void SetInstalled(string id, bool installed)
    {
        Find(id).IsInstalled = installed;
        Notify();
    }

    public void MarkInstalled(string id)
    {
        Find(id).IsInstalled = true;
        Notify();
    }

    private DependencySelection Find(string id) =>
        Dependencies.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Unknown dependency '{id}'.");

    private void Notify()
    {
        OnPropertyChanged(nameof(Dependencies));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ReviewText));
    }
}
