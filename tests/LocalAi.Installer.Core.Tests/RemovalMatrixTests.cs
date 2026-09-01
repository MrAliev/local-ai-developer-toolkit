using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The matrix is the contract; the presets are prefilled checkboxes over it and nothing else.
///
/// That distinction is the whole design of the removal page, and it is exactly the kind of
/// claim that decays into three separate code paths the moment nothing checks it — at which
/// point "reinstall-friendly" quietly means something other than the same plan with different
/// boxes ticked.
/// </summary>
public sealed class RemovalMatrixTests
{
    [Fact]
    public void Every_preset_is_the_same_plan_with_different_boxes_ticked()
    {
        foreach (var preset in RemovalMatrix.Presets)
        {
            var selection = RemovalSelection.FromPreset(preset);
            foreach (var item in RemovalMatrix.Items)
            {
                var expected =
                    item != RemovalItem.SigningKeys &&
                    RemovalMatrix.Disposition(preset, item) == RemovalDisposition.Remove;

                Assert.Equal(expected, selection.Includes(item));
            }
        }
    }

    [Fact]
    public void A_full_uninstall_takes_everything_except_the_keys()
    {
        var selection = RemovalSelection.FromPreset(RemovalPreset.FullUninstall);

        Assert.All(
            RemovalMatrix.Items.Where(item => item != RemovalItem.SigningKeys),
            item => Assert.True(selection.Includes(item), RemovalMatrix.Title(item)));
        Assert.False(selection.Includes(RemovalItem.SigningKeys));
    }

    [Fact]
    public void Disconnecting_clients_leaves_the_runtime_alone()
    {
        var selection = RemovalSelection.FromPreset(RemovalPreset.DisconnectClients);

        Assert.True(selection.Includes(RemovalItem.ClaudeIntegration));
        Assert.True(selection.Includes(RemovalItem.CodexIntegration));
        Assert.True(selection.Includes(RemovalItem.GitHooks));
        Assert.False(selection.Includes(RemovalItem.Binaries));
        Assert.False(selection.Includes(RemovalItem.RepositoryIndexes));
        Assert.False(selection.Includes(RemovalItem.Settings));
        Assert.False(selection.Includes(RemovalItem.TransientState));
        Assert.False(selection.Includes(RemovalItem.OtherRuntimeFiles));
    }

    /// <summary>
    /// The point of this preset: the binaries go, and the two things that cost hours to
    /// produce stay. The clients are left to the person, because "am I coming back to this
    /// machine" is not a question the preset can answer for them.
    /// </summary>
    [Fact]
    public void Reinstall_friendly_keeps_what_a_reinstall_would_honour()
    {
        var selection = RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly);

        Assert.True(selection.Includes(RemovalItem.Binaries));
        Assert.True(selection.Includes(RemovalItem.TransientState));
        Assert.False(selection.Includes(RemovalItem.RepositoryIndexes));
        Assert.False(selection.Includes(RemovalItem.Settings));
        // The client registrations and the hooks are kept rather than asked about: the
        // installation that follows a reinstall rewrites all three, so asking is putting the
        // same question twice and letting the two answers disagree.
        Assert.False(selection.Includes(RemovalItem.ClaudeIntegration));
        Assert.False(selection.Includes(RemovalItem.CodexIntegration));
        Assert.False(selection.Includes(RemovalItem.GitHooks));
        Assert.Empty(selection.ItemsNeedingDecision);
    }

    [Fact]
    public void A_row_the_preset_left_open_is_prefilled_as_kept()
    {
        // Full uninstall is the preset that still leaves one open — the signing keys, which
        // no preset may remove on somebody's behalf.
        var selection = RemovalSelection.FromPreset(RemovalPreset.FullUninstall);
        Assert.NotEmpty(selection.ItemsNeedingDecision);

        Assert.All(
            selection.ItemsNeedingDecision,
            item => Assert.False(selection.Includes(item), RemovalMatrix.Title(item)));
    }

    [Fact]
    public void The_keys_need_their_own_confirmation_and_cannot_be_ticked_like_a_row()
    {
        var selection = RemovalSelection.FromPreset(RemovalPreset.FullUninstall);

        var refusal = Assert.Throws<ArgumentException>(() =>
            selection.With(RemovalItem.SigningKeys, true));

        Assert.Contains("confirmation", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(selection.Includes(RemovalItem.SigningKeys));
        Assert.True(selection.WithSigningKeyRemoval(true).Includes(RemovalItem.SigningKeys));
        Assert.False(
            selection.WithSigningKeyRemoval(true).WithSigningKeyRemoval(false)
                .Includes(RemovalItem.SigningKeys));
    }

    [Fact]
    public void Any_row_can_be_changed_after_a_preset_filled_it_in()
    {
        var selection = RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)
            .With(RemovalItem.Binaries, true)
            .With(RemovalItem.ClaudeIntegration, false);

        Assert.True(selection.Includes(RemovalItem.Binaries));
        Assert.False(selection.Includes(RemovalItem.ClaudeIntegration));
        Assert.True(selection.Includes(RemovalItem.CodexIntegration));
        Assert.Equal(RemovalPreset.DisconnectClients, selection.Preset);
    }

    [Fact]
    public void Hooks_default_to_every_connected_repository_and_can_be_narrowed()
    {
        var selection = RemovalSelection.FromPreset(RemovalPreset.FullUninstall);

        Assert.True(selection.IncludesRepository("abc"));
        Assert.True(selection.WithRepositories(["abc"]).IncludesRepository("abc"));
        Assert.False(selection.WithRepositories(["abc"]).IncludesRepository("def"));
        Assert.True(selection.WithRepositories(["abc"]).WithRepositories(null).IncludesRepository("def"));
        Assert.False(
            selection.With(RemovalItem.GitHooks, false).IncludesRepository("abc"));
    }

    [Theory]
    [InlineData("bin", RemovalItem.Binaries)]
    [InlineData("installer", RemovalItem.Binaries)]
    [InlineData("repositories", RemovalItem.RepositoryIndexes)]
    [InlineData("policy.json", RemovalItem.Settings)]
    [InlineData("semantic-indexing.json", RemovalItem.Settings)]
    [InlineData("jobs", RemovalItem.TransientState)]
    [InlineData("telemetry", RemovalItem.TransientState)]
    [InlineData("host.json", RemovalItem.TransientState)]
    [InlineData("release-signing", RemovalItem.SigningKeys)]
    // Whatever the runtime writes next belongs to somebody, and a matrix that silently kept it
    // would make a full uninstall incomplete without saying so.
    [InlineData("something-a-later-release-added.json", RemovalItem.OtherRuntimeFiles)]
    public void Each_runtime_root_entry_belongs_to_exactly_one_row(
        string name,
        RemovalItem expected) =>
        Assert.Equal(expected, RemovalMatrix.ClassifyRootEntry(name));

    [Fact]
    public void Every_settings_file_is_classified_as_one()
    {
        Assert.All(
            RemovalMatrix.SettingsFileNames,
            name => Assert.Equal(RemovalItem.Settings, RemovalMatrix.ClassifyRootEntry(name)));
    }

    /// <summary>
    /// The settings are named here as strings, because the stores that own them live in
    /// CodeSearch.Core and LocalLm.Core — executables' libraries this project deliberately does
    /// not reference. A copied name is only safe while something notices it drifting, so this
    /// reads the declarations themselves.
    /// </summary>
    [Theory]
    [InlineData("src/LocalAi.Contracts/ModelResidencyPolicyStore.cs", "policy.json")]
    [InlineData("src/LocalAi.Contracts/RuntimeRetentionPolicy.cs", "retention.json")]
    [InlineData("src/LocalLm.Core/LogTriagePolicy.cs", "log-triage.json")]
    [InlineData("src/CodeSearch.Core/Semantics/LanguageServerPolicyStore.cs", "language-servers.json")]
    [InlineData("src/CodeSearch.Core/Semantics/SemanticIndexingPolicyStore.cs", "semantic-indexing.json")]
    [InlineData("src/LocalAi.Contracts/UpdateCheckPolicy.cs", "update-check.json")]
    public void The_settings_file_names_match_the_stores_that_write_them(
        string sourcePath,
        string fileName)
    {
        var declaration = File.ReadAllText(Path.Combine(RepositoryRoot(), sourcePath));

        Assert.Contains(
            "FileName = \"" + fileName + "\"",
            declaration,
            StringComparison.Ordinal);
        Assert.Contains(fileName, RemovalMatrix.SettingsFileNames);
    }

    [Fact]
    public void Every_row_and_preset_can_say_what_it_is()
    {
        Assert.All(RemovalMatrix.Items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(RemovalMatrix.Title(item)));
            Assert.False(string.IsNullOrWhiteSpace(RemovalMatrix.Note(item)));
        });
        Assert.All(RemovalMatrix.Presets, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(RemovalMatrix.Title(preset)));
            Assert.False(string.IsNullOrWhiteSpace(RemovalMatrix.Description(preset)));
        });
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
