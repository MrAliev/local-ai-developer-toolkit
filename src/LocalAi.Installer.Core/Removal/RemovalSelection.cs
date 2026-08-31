namespace LocalAi.Installer.Core.Removal;

/// <summary>
/// What this particular removal was asked to take away.
///
/// A preset fills it in; every row can then be changed on its own, which is what makes the
/// matrix the contract and the presets a convenience. Two rows do not behave like the rest,
/// deliberately:
///
/// The signing keys cannot be selected by <see cref="With"/> at all. Losing them makes an
/// offline backup the only copy in existence, so they need a second, separate confirmation —
/// and an API where that is just another boolean is an API where one wrong argument destroys
/// them.
///
/// The Git hooks are per repository. A selection that names no repositories means every
/// repository the runtime manifests know about, which is what a full uninstall means by it;
/// naming them narrows it to those.
/// </summary>
public sealed class RemovalSelection
{
    private readonly IReadOnlyDictionary<RemovalItem, bool> chosen;
    private readonly IReadOnlySet<string>? repositories;

    private RemovalSelection(
        RemovalPreset? preset,
        IReadOnlyDictionary<RemovalItem, bool> chosen,
        bool signingKeyRemovalConfirmed,
        IReadOnlySet<string>? repositories)
    {
        Preset = preset;
        this.chosen = chosen;
        SigningKeyRemovalConfirmed = signingKeyRemovalConfirmed;
        this.repositories = repositories;
    }

    /// <summary>The preset these choices started from, for the journal and the review page.</summary>
    public RemovalPreset? Preset { get; }

    public bool SigningKeyRemovalConfirmed { get; }

    /// <summary>
    /// The rows this preset deliberately left to the person. They are prefilled as kept, and a
    /// review page shows them as decisions rather than as defaults.
    /// </summary>
    public IReadOnlyList<RemovalItem> ItemsNeedingDecision =>
        Preset is null
            ? []
            : RemovalMatrix.Items
                .Where(item => RemovalMatrix.Disposition(Preset.Value, item) == RemovalDisposition.Ask)
                .ToArray();

    public static RemovalSelection FromPreset(RemovalPreset preset)
    {
        var chosen = RemovalMatrix.Items.ToDictionary(
            item => item,
            item => RemovalMatrix.Disposition(preset, item) == RemovalDisposition.Remove);
        // Ask and Keep both prefill as kept: an uninstall never removes something by default
        // that the preset declined to take a position on.
        chosen[RemovalItem.SigningKeys] = false;
        return new(preset, chosen, false, null);
    }

    /// <summary>Nothing selected: the starting point for a wholly hand-made selection.</summary>
    public static RemovalSelection Nothing() =>
        new(null, RemovalMatrix.Items.ToDictionary(item => item, _ => false), false, null);

    public bool Includes(RemovalItem item) =>
        item == RemovalItem.SigningKeys
            ? SigningKeyRemovalConfirmed
            : chosen.TryGetValue(item, out var selected) && selected;

    public RemovalSelection With(RemovalItem item, bool remove)
    {
        if (item == RemovalItem.SigningKeys)
        {
            throw new ArgumentException(
                "The signing key directory is removed only through " +
                nameof(WithSigningKeyRemoval) + ", which is the separate confirmation it needs.",
                nameof(item));
        }

        var updated = chosen.ToDictionary(entry => entry.Key, entry => entry.Value);
        updated[item] = remove;
        return new(Preset, updated, SigningKeyRemovalConfirmed, repositories);
    }

    /// <summary>
    /// Records the separate, explicit confirmation that the signing keys are to go — the only
    /// way this selection can ever include them.
    /// </summary>
    public RemovalSelection WithSigningKeyRemoval(bool confirmed) =>
        new(Preset, chosen, confirmed, repositories);

    /// <summary>
    /// Narrows hook removal to the named repositories. Passing null restores the default,
    /// which is every repository the runtime manifests name.
    /// </summary>
    public RemovalSelection WithRepositories(IEnumerable<string>? repositoryIds) =>
        new(
            Preset,
            chosen,
            SigningKeyRemovalConfirmed,
            repositoryIds is null
                ? null
                : new HashSet<string>(repositoryIds, StringComparer.OrdinalIgnoreCase));

    public bool IncludesRepository(string repositoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        return Includes(RemovalItem.GitHooks) &&
            (repositories is null || repositories.Contains(repositoryId));
    }
}
