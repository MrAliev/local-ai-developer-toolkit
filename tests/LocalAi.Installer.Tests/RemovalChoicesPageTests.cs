using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The matrix page on its own, without a wizard around it.
///
/// It was inside <see cref="UninstallWizardViewModel"/> and is about to be used by the
/// installer wizard too, for the removal half of a clean reinstall. What these pin is the part
/// that must not differ between the two callers: which boxes a preset ticks, and what the
/// selection handed to the planner says.
/// </summary>
public sealed class RemovalChoicesPageTests
{
    [Fact]
    public void A_preset_fills_the_boxes_in()
    {
        var page = new RemovalChoicesPageViewModel(RemovalPreset.ReinstallFriendly);

        Assert.True(Row(page, RemovalItem.Binaries).IsSelected);
        Assert.False(Row(page, RemovalItem.RepositoryIndexes).IsSelected);
        Assert.True(page.Presets.Single(option => option.IsSelected).Preset
            == RemovalPreset.ReinstallFriendly);
    }

    [Fact]
    public void A_row_changed_by_hand_is_what_the_selection_says()
    {
        var page = new RemovalChoicesPageViewModel(RemovalPreset.DisconnectClients);
        Row(page, RemovalItem.Binaries).IsSelected = true;
        Row(page, RemovalItem.CodexIntegration).IsSelected = false;

        var selection = page.Selection();

        Assert.True(selection.Includes(RemovalItem.Binaries));
        Assert.False(selection.Includes(RemovalItem.CodexIntegration));
        Assert.True(selection.Includes(RemovalItem.ClaudeIntegration));
    }

    /// <summary>
    /// Switching preset re-fills every box, including ones changed by hand — that is what a
    /// preset is. The keys are the exception: no preset may ever prefill them.
    /// </summary>
    [Fact]
    public void Switching_preset_refills_everything_and_never_the_keys()
    {
        var page = new RemovalChoicesPageViewModel(RemovalPreset.DisconnectClients)
        {
            RemoveSigningKeys = true,
        };
        Row(page, RemovalItem.Binaries).IsSelected = true;

        page.SelectedPreset = RemovalPreset.FullUninstall;

        Assert.False(page.RemoveSigningKeys);
        Assert.False(page.Selection().Includes(RemovalItem.SigningKeys));
        Assert.True(page.Selection().Includes(RemovalItem.Binaries));
    }

    [Fact]
    public void The_keys_reach_the_selection_only_through_their_own_confirmation()
    {
        var page = new RemovalChoicesPageViewModel();
        Assert.False(page.Selection().Includes(RemovalItem.SigningKeys));

        page.RemoveSigningKeys = true;

        Assert.True(page.Selection().Includes(RemovalItem.SigningKeys));
    }

    /// <summary>
    /// No repositories listed means the hooks are not narrowed to a subset — every connected
    /// repository is in scope. Narrowing to none would be a different plan silently.
    /// </summary>
    [Fact]
    public void Listed_repositories_narrow_the_hooks_and_an_empty_list_does_not()
    {
        var page = new RemovalChoicesPageViewModel();
        Assert.True(page.Selection().IncludesRepository("anything"));

        page.ListRepositories([
            new RepositoryRow("abc", @"C:\one\.git", 3, null),
            new RepositoryRow("def", @"C:\two\.git", 1, null),
        ]);
        page.Repositories.Single(row => row.RepositoryId == "def").IsSelected = false;

        Assert.True(page.Selection().IncludesRepository("abc"));
        Assert.False(page.Selection().IncludesRepository("def"));
    }

    /// <summary>
    /// The page is built before anybody says which installation it is looking at, so the
    /// warning names the directory only once it has been told.
    /// </summary>
    [Fact]
    public void The_key_warning_names_the_directory_it_was_given()
    {
        var page = new RemovalChoicesPageViewModel
        {
            SigningKeysDirectory = @"C:\somewhere\release-signing",
        };

        Assert.Contains(@"C:\somewhere\release-signing", page.SigningKeysWarning, StringComparison.Ordinal);
        Assert.Contains("only copy", page.SigningKeysWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A change on the page has to reach whatever is around it: the wizard rebuilds its
    /// buttons and drops any plan built from the old answers.
    /// </summary>
    [Fact]
    public void Every_choice_announces_itself()
    {
        var page = new RemovalChoicesPageViewModel();
        var changes = 0;
        page.Changed += (_, _) => changes++;

        page.SelectedPreset = RemovalPreset.ReinstallFriendly;
        page.RemoveSigningKeys = true;

        Assert.Equal(2, changes);
    }

    private static RemovalRow Row(RemovalChoicesPageViewModel page, RemovalItem item) =>
        page.Rows.Single(row => row.Item == item);
}
