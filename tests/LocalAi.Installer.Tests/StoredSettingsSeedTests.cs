using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The two settings an update used to reset without saying so (#256).
///
/// The wizard wrote both from its own defaults at the end of every run and never read what
/// was on disk, so a machine configured for `AllowCpu` came back from an upgrade requiring
/// full video memory, and an enabled update check came back off. The review page listed the
/// value it was about to write, which reads as "what will be configured" rather than "what is
/// being replaced".
///
/// These tests work through the writers rather than the WPF view model, because what has to
/// hold is the round trip: a value on disk survives a run that does not touch its page.
/// </summary>
public sealed class StoredSettingsSeedTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-stored-settings-" + Guid.NewGuid().ToString("N"));

    public StoredSettingsSeedTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(ModelResidencyPolicy.AllowCpu)]
    [InlineData(ModelResidencyPolicy.AllowPartialOffload)]
    public void A_relaxed_residency_survives_a_run_that_writes_it_back(
        ModelResidencyPolicy stored)
    {
        var store = new ModelResidencyPolicyStore(root);
        store.Write(store.Read() with { ModelResidency = stored });

        // What the wizard now does: read first, then write what it read.
        var seeded = store.Read().ModelResidency;
        ResidencyPolicyWriter.Apply(root, seeded);

        Assert.Equal(stored, store.Read().ModelResidency);
    }

    [Fact]
    public void An_enabled_update_check_survives_a_run_that_writes_it_back()
    {
        var store = new UpdateCheckPolicyStore(root);
        store.Write(UpdateCheckPolicy.Default with { Enabled = true, IntervalHours = 6 });

        var seeded = store.Read().Enabled;
        UpdateCheckPolicyWriter.Apply(root, seeded);

        var policy = store.Read();
        Assert.True(policy.Enabled);
        Assert.Equal(6, policy.IntervalHours);
    }

    /// <summary>
    /// The rule seeding must not break: an unreadable update-check policy means off, so a
    /// corrupt file can never become permission to use the network.
    /// </summary>
    [Fact]
    public void A_corrupt_update_check_policy_seeds_nothing_and_stays_off()
    {
        File.WriteAllText(Path.Combine(root, UpdateCheckPolicy.FileName), "{ not json");

        var seeded = new UpdateCheckPolicyStore(root).Read().Enabled;

        Assert.False(seeded);
    }

    /// <summary>
    /// Turning it off is still a write. Somebody who unticks the box on a reinstall is
    /// changing a setting that already says yes.
    /// </summary>
    [Fact]
    public void Declining_still_clears_a_stored_yes()
    {
        var store = new UpdateCheckPolicyStore(root);
        store.Write(UpdateCheckPolicy.Default with { Enabled = true });

        UpdateCheckPolicyWriter.Apply(root, enabled: false);

        Assert.False(store.Read().Enabled);
    }

    /// <summary>
    /// The consent list is read by a person, so it carries the rule in the words the page used
    /// to offer it — not the name the enum happens to have. "Model residency: RequireFullVram"
    /// put an identifier in front of somebody about to agree to it.
    /// </summary>
    [Theory]
    [InlineData(ModelResidencyPolicy.RequireFullVram, "whole model in video memory")]
    [InlineData(ModelResidencyPolicy.AllowPartialOffload, "part of the model in system memory")]
    [InlineData(ModelResidencyPolicy.AllowCpu, "running on the processor")]
    public void The_review_names_the_rule_rather_than_the_enum(
        ModelResidencyPolicy policy,
        string expected)
    {
        var page = new LocalAi.Installer.ViewModels.ResidencyPageViewModel { Policy = policy };

        Assert.Equal("Video memory: " + expected, page.ReviewText);
        Assert.DoesNotContain(policy.ToString(), page.ReviewText, StringComparison.Ordinal);
    }
}
