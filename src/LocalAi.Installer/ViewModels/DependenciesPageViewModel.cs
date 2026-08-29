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
    ///
    /// Two are required, and only two. Git is how a repository is identified, scanned and
    /// hooked; Ollama is what computes the embeddings, without which there is no index at
    /// all. Everything else buys precise navigation for one language, and demanding all of
    /// it cost three to four gigabytes and several UAC prompts — winget installs these
    /// machine-wide — from somebody who may only want semantic search over C#. Each optional
    /// line therefore says what skipping it gives up, because "optional" alone invites
    /// skipping everything and discovering the cost at the moment a tool stops answering
    /// precisely.
    /// </summary>
    public ObservableCollection<DependencySelection> Dependencies { get; } =
    [
        new("Git", "Git", true),
        new("Ollama", "Ollama", true),
        new("GitHubCli", "GitHub CLI", false)
        {
            // The repository is public, so releases are read over plain HTTPS with no
            // account at all. Kept for a fork held private, or a network that blocks the
            // release host but not the API.
            Consequence = "only needed for a private fork, or when the release host is blocked",
        },
        new("DotNetSdk", ".NET SDK 10", false)
        {
            Consequence =
                "without it, C# definitions and references are answered by text matching "
                + "instead of by the compiler",
        },
        new("NodeJs", "Node.js 20", false)
        {
            Consequence = "only needed to run the TypeScript indexer",
        },
        new("ScipTypeScript", "SCIP TypeScript", false)
        {
            Consequence = "without it, TypeScript and JavaScript navigate by text matching",
        },
        new("Python", "Python 3.10+", false)
        {
            Consequence = "only needed to run the Python indexer",
        },
        new("ScipPython", "SCIP Python", false)
        {
            Consequence = "without it, Python navigates by text matching",
        },
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
