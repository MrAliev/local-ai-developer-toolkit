using System.Collections.ObjectModel;

namespace LocalAi.Installer.ViewModels;

public sealed class DependenciesPageViewModel : ObservableObject
{
    /// <summary>
    /// Only what the installer can act on and the product actually needs.
    ///
    /// The MSVC redistributable used to be listed here. Nothing in LocalAi referenced it —
    /// no catalogue entry, no detection, no code requiring it. The components are
    /// self-contained .NET and Ollama brings its own native dependencies, so it has been
    /// removed rather than left as a line that informs nobody and installs nothing.
    /// </summary>
    public ObservableCollection<DependencySelection> Dependencies { get; } =
    [
        new("Git", "Git", true),
        new("Ollama", "Ollama", true),
        // Optional, and deliberately so. The repository is public, so releases are read
        // over plain HTTPS with no account at all. The CLI is kept as a fallback — a fork
        // kept private is still installable through an existing 'gh auth login', and a
        // network that blocks the release host may not block the API — but demanding it
        // from someone installing a published tool asks for an account they do not need.
        new("GitHubCli", "GitHub CLI", false),
        new("DotNetSdk", ".NET SDK 10", true),
        new("NodeJs", "Node.js 20", true),
        new("ScipTypeScript", "SCIP TypeScript", true),
        new("Python", "Python 3.10+", true),
        new("ScipPython", "SCIP Python", true),
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
