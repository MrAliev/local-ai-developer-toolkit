using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The wizard in uninstall mode, over the same installed machine the removal core is tested
/// against. What these pin is the promise the page makes: nothing is removed before the
/// confirmation, and what the review page listed is what runs.
/// </summary>
public sealed class UninstallWizardViewModelTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    private readonly string journalDirectory = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.RemovalWizard",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        machine.Dispose();
        try
        {
            Directory.Delete(journalDirectory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task The_page_opens_on_a_full_uninstall_with_the_keys_untouched()
    {
        var wizard = await Wizard();

        Assert.Equal(RemovalPreset.FullUninstall, wizard.SelectedPreset);
        Assert.All(wizard.Rows, row => Assert.True(row.IsSelected, row.Title));
        Assert.False(wizard.RemoveSigningKeys);
        Assert.DoesNotContain(wizard.Rows, row => row.Item == RemovalItem.SigningKeys);
        Assert.Contains("only copy", wizard.SigningKeysWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_preset_refills_the_boxes_and_says_what_it_left_open()
    {
        var wizard = await Wizard();

        wizard.SelectedPreset = RemovalPreset.ReinstallFriendly;

        Assert.True(Row(wizard, RemovalItem.Binaries).IsSelected);
        Assert.False(Row(wizard, RemovalItem.RepositoryIndexes).IsSelected);
        Assert.False(Row(wizard, RemovalItem.Settings).IsSelected);
        Assert.True(Row(wizard, RemovalItem.ClaudeIntegration).NeedsDecision);
        Assert.False(Row(wizard, RemovalItem.ClaudeIntegration).IsSelected);
        Assert.Equal("your choice", Row(wizard, RemovalItem.ClaudeIntegration).DecisionText);
        Assert.False(Row(wizard, RemovalItem.Binaries).NeedsDecision);
    }

    [Fact]
    public async Task A_row_changed_by_hand_is_what_the_plan_uses()
    {
        var wizard = await Wizard();
        wizard.SelectedPreset = RemovalPreset.DisconnectClients;
        Row(wizard, RemovalItem.Binaries).IsSelected = true;
        Row(wizard, RemovalItem.CodexIntegration).IsSelected = false;

        var selection = wizard.Selection();

        Assert.True(selection.Includes(RemovalItem.Binaries));
        Assert.True(selection.Includes(RemovalItem.ClaudeIntegration));
        Assert.False(selection.Includes(RemovalItem.CodexIntegration));
        Assert.False(selection.Includes(RemovalItem.SigningKeys));
    }

    [Fact]
    public async Task The_keys_reach_the_plan_only_through_their_own_checkbox()
    {
        var wizard = await Wizard();

        Assert.False(wizard.Selection().Includes(RemovalItem.SigningKeys));

        wizard.RemoveSigningKeys = true;

        Assert.True(wizard.Selection().Includes(RemovalItem.SigningKeys));
    }

    [Fact]
    public async Task The_connected_repositories_are_listed_before_anything_is_chosen()
    {
        var wizard = await Wizard();

        Assert.Equal(
            ["gone", "husky", "plain"],
            wizard.Repositories.Select(repository => repository.RepositoryId).Order());
        var missing = wizard.Repositories.Single(repository => repository.RepositoryId == "gone");
        Assert.False(missing.CanChoose);
        Assert.False(missing.IsSelected);
        Assert.Contains("skipped", missing.StateText, StringComparison.Ordinal);
        Assert.Contains(
            "2 hook(s) installed",
            wizard.Repositories.Single(repository => repository.RepositoryId == "plain").StateText,
            StringComparison.Ordinal);
        // Found where core.hooksPath sends the search rather than in $GIT_DIR/hooks: a wizard
        // that looked only in the default place would report this repository as clean.
        var husky = wizard.Repositories.Single(repository => repository.RepositoryId == "husky");
        Assert.Contains("1 hook(s) installed", husky.StateText, StringComparison.Ordinal);
        Assert.True(husky.IsSelected);
    }

    [Fact]
    public async Task A_repository_can_be_left_out_on_its_own()
    {
        var wizard = await Wizard();
        wizard.Repositories.Single(repository => repository.RepositoryId == "plain")
            .IsSelected = false;

        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(machine.PlainHooks, wizard.PreviewText, StringComparison.Ordinal);
        Assert.Contains(machine.HuskyHooks, wizard.PreviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule the whole page exists for: reaching the review page changes nothing on disk,
    /// and the Uninstall button stays out of reach until the person says they have read it.
    /// </summary>
    [Fact]
    public async Task Nothing_is_removed_before_the_confirmation()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var wizard = await Wizard();

        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UninstallPage.Confirm, wizard.CurrentPage);
        Assert.Contains(machine.Runtime, wizard.PreviewText, StringComparison.Ordinal);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        Assert.False(wizard.CanRun);

        wizard.IsConfirmed = true;

        Assert.True(wizard.CanRun);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
    }

    [Fact]
    public async Task Going_back_takes_the_confirmation_with_it()
    {
        var wizard = await Wizard();
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.True(wizard.MovePrevious());

        Assert.Equal(UninstallPage.Choose, wizard.CurrentPage);
        Assert.False(wizard.IsConfirmed);
        Assert.False(wizard.CanRun);
    }

    [Fact]
    public async Task The_run_performs_what_the_review_page_listed()
    {
        var wizard = await Wizard();
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;
        var preview = wizard.PreviewText;

        var succeeded = await wizard.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(succeeded);
        Assert.Equal(UninstallPage.Finish, wizard.CurrentPage);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
        // Kept, and the preview said so, because a full uninstall does not take the keys.
        Assert.True(Directory.Exists(Path.Combine(
            machine.Runtime,
            RemovalMatrix.SigningKeyDirectoryName)));
        Assert.Contains("keep", preview, StringComparison.Ordinal);
        Assert.Contains("winget uninstall", wizard.Report, StringComparison.Ordinal);
        Assert.Contains("ollama rm", wizard.Report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_stop_ends_the_run_saying_the_machine_is_unchanged()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var wizard = await Wizard(new StubProcessRunner
        {
            Result = new ProcessResult(1, string.Empty, "broker_still_running", false, false),
        });
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        var succeeded = await wizard.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(succeeded);
        Assert.True(wizard.HasRunError);
        Assert.Equal(UninstallPage.Finish, wizard.CurrentPage);
        Assert.Contains("nothing was removed", wizard.Summary, StringComparison.Ordinal);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
    }

    /// <summary>
    /// One wizard writing the runtime while another removes it leaves a tree neither of them
    /// describes. The live lock the journal already keeps is what recognises the other one.
    /// </summary>
    [Fact]
    public async Task A_second_wizard_is_refused_while_another_run_holds_its_lock()
    {
        using var other = InstallerRunJournal.Start(journalDirectory);

        var wizard = await Wizard();

        Assert.True(wizard.IsBlocked);
        Assert.Contains("Another LocalAi installer", wizard.BlockingNotice!, StringComparison.Ordinal);
        Assert.False(wizard.CanMoveNext);
        Assert.Empty(wizard.Repositories);
    }

    [Fact]
    public async Task A_finished_run_no_longer_blocks_the_next_wizard()
    {
        using (var previous = InstallerRunJournal.Start(journalDirectory))
        {
            previous.Finish(InstallerRunOutcome.Completed);
        }

        var wizard = await Wizard();

        Assert.False(wizard.IsBlocked);
        Assert.True(wizard.CanMoveNext);
    }

    [Fact]
    public async Task With_nothing_installed_the_wizard_says_so_instead_of_planning()
    {
        Directory.Delete(machine.Runtime, recursive: true);

        var wizard = await Wizard();

        Assert.True(wizard.IsBlocked);
        Assert.Contains("no LocalAi installation", wizard.BlockingNotice!, StringComparison.Ordinal);
        Assert.False(wizard.CanMoveNext);
    }

    private static RemovalRow Row(UninstallWizardViewModel wizard, RemovalItem item) =>
        wizard.Rows.Single(row => row.Item == item);

    private async Task<UninstallWizardViewModel> Wizard(IProcessRunner? processRunner = null)
    {
        var wizard = new UninstallWizardViewModel(
            machine.Layout,
            machine.Home,
            processRunner ?? new StubProcessRunner(),
            machine.HooksPathReader)
        {
            LogDirectory = journalDirectory,
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return wizard;
    }

    /// <summary>Stands in for the launcher, which is the only process this wizard starts.</summary>
    private sealed class StubProcessRunner : IProcessRunner
    {
        public ProcessResult Result { get; set; } =
            new(0, string.Empty, string.Empty, false, false);

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }
}
