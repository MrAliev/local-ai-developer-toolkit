using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The uninstall wizard's pages, in navigation order. Same shape as the installation's: the
/// choices, then a confirmation that is a pure milestone, then the work, then what happened.
/// </summary>
public enum UninstallPage
{
    Choose,
    Confirm,
    Progress,
    Finish,
}

/// <summary>
/// One row of the removal matrix as the page shows it. The checkbox is the whole interface:
/// a preset ticks a set of these, and changing one afterwards is expected rather than an
/// escape hatch.
/// </summary>
public sealed class RemovalRow(RemovalItem item) : ObservableObject
{
    private bool isSelected;

    public RemovalItem Item { get; } = item;

    public string Title { get; } = RemovalMatrix.Title(item);

    public string Note { get; } = RemovalMatrix.Note(item);

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    /// <summary>
    /// Whether the current preset declined to take a position on this row. Shown, because a
    /// row prefilled as kept because nobody decided looks exactly like one prefilled as kept
    /// on purpose.
    /// </summary>
    public bool NeedsDecision { get; set; }

    public string DecisionText => NeedsDecision ? "your choice" : string.Empty;
}

/// <summary>One connected repository, and whether its dispatchers are to be taken out.</summary>
public sealed class RepositoryRow(
    string repositoryId,
    string commonDirectory,
    int dispatcherCount,
    string? skipReason) : ObservableObject
{
    private bool isSelected = skipReason is null && dispatcherCount > 0;

    public string RepositoryId { get; } = repositoryId;

    public string CommonDirectory { get; } = commonDirectory;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public bool CanChoose => skipReason is null && dispatcherCount > 0;

    public string StateText => skipReason is { Length: > 0 } reason
        ? "skipped — " + reason
        : dispatcherCount == 0
            ? "no LocalAi hooks found"
            : dispatcherCount + " hook(s) installed";
}

public sealed record UninstallPresetOption(RemovalPreset Preset)
{
    public string Title => RemovalMatrix.Title(Preset);

    public string Description => RemovalMatrix.Description(Preset);
}
