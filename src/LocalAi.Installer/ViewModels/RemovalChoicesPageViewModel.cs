using System.Collections.ObjectModel;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The removal matrix as a page: the presets, the rows they fill in, the repositories the
/// dispatchers live in, and the separate confirmation the signing keys need.
///
/// Lifted out of <see cref="UninstallWizardViewModel"/> because a clean reinstall needs the
/// same page inside the installer wizard. Two copies of a matrix UI is two copies that drift,
/// and the thing that would drift is which boxes a preset ticks — which is the whole contract
/// between this page and the planner.
/// </summary>
public sealed class RemovalChoicesPageViewModel : ObservableObject
{
    private RemovalPreset selectedPreset;
    private bool removeSigningKeys;

    public RemovalChoicesPageViewModel(RemovalPreset preset = RemovalPreset.FullUninstall)
    {
        selectedPreset = preset;
        foreach (var item in RemovalMatrix.Items.Where(item => item != RemovalItem.SigningKeys))
        {
            Rows.Add(new RemovalRow(item));
        }

        ApplyPreset(selectedPreset);
    }

    /// <summary>Raised whenever a choice on this page changes what the plan would be.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<RemovalRow> Rows { get; } = [];

    public ObservableCollection<RepositoryRow> Repositories { get; } = [];

    public IReadOnlyList<UninstallPresetOption> Presets { get; } = RemovalMatrix.Presets
        .Select(preset => new UninstallPresetOption(preset))
        .ToArray();

    public RemovalPreset SelectedPreset
    {
        get => selectedPreset;
        set
        {
            if (selectedPreset == value)
            {
                return;
            }

            selectedPreset = value;
            ApplyPreset(value);
            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The separate confirmation the key directory needs. Kept off the matrix rows on purpose:
    /// with it ticked the offline backup becomes the only copy that exists anywhere.
    /// </summary>
    public bool RemoveSigningKeys
    {
        get => removeSigningKeys;
        set
        {
            SetProperty(ref removeSigningKeys, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SigningKeysTitle => RemovalMatrix.Title(RemovalItem.SigningKeys);

    /// <summary>
    /// Set once the layout is known. The warning names the directory, and the page is built
    /// before anybody has told it which installation it is looking at.
    /// </summary>
    public string SigningKeysDirectory { get; set; } = string.Empty;

    // The one line where the path cannot stay in front: Russian puts it behind a preposition.
    public string SigningKeysWarning => string.Format(
        InstallerCulture.Pick(
            "{0} holds the private half of the release signing pair. Remove it only if you " +
            "have the offline backup: it becomes the only copy that exists.",
            "В {0} лежит закрытая половина пары ключей подписи релизов. Удаляйте, " +
            "только если у вас есть офлайн-копия: она останется единственной."),
        SigningKeysDirectory);

    /// <summary>The repositories the dispatchers were found in, listed before anybody chooses.</summary>
    public void ListRepositories(IEnumerable<RepositoryRow> repositories)
    {
        Repositories.Clear();
        foreach (var repository in repositories)
        {
            Repositories.Add(repository);
        }
    }

    /// <summary>The choices as the core understands them.</summary>
    public RemovalSelection Selection()
    {
        var selection = RemovalSelection.FromPreset(selectedPreset);
        foreach (var row in Rows)
        {
            selection = selection.With(row.Item, row.IsSelected);
        }

        selection = selection.WithSigningKeyRemoval(removeSigningKeys);
        return Repositories.Count == 0
            ? selection
            : selection.WithRepositories(Repositories
                .Where(repository => repository.IsSelected)
                .Select(repository => repository.RepositoryId));
    }

    private void ApplyPreset(RemovalPreset preset)
    {
        foreach (var option in Presets)
        {
            option.IsSelected = option.Preset == preset;
        }

        var selection = RemovalSelection.FromPreset(preset);
        var undecided = selection.ItemsNeedingDecision.ToHashSet();
        foreach (var row in Rows)
        {
            row.IsSelected = selection.Includes(row.Item);
            row.NeedsDecision = undecided.Contains(row.Item);
        }

        // The keys are never prefilled by a preset. Their checkbox is the confirmation.
        removeSigningKeys = false;
        OnPropertyChanged(nameof(RemoveSigningKeys));
    }
}
