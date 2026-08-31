using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Storing the update-check answer, under the rule that cost a machine every future
/// installation once: a policy write must never be the thing that creates the LocalAi root,
/// because a root created by an ordinary CreateDirectory inherits access rules the layout
/// lease refuses forever.
/// </summary>
public sealed class UpdateCheckPolicyWriterTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-update-writer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void A_machine_without_an_installation_is_left_exactly_as_it_was()
    {
        var outcome = UpdateCheckPolicyWriter.Apply(root, enabled: true);

        Assert.Equal(ResidencyPolicyOutcome.SkippedWithoutInstallation, outcome);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void An_installed_machine_records_the_answer()
    {
        Directory.CreateDirectory(root);

        var outcome = UpdateCheckPolicyWriter.Apply(root, enabled: true);

        Assert.Equal(ResidencyPolicyOutcome.Applied, outcome);
        Assert.True(new UpdateCheckPolicyStore(root).Read().Enabled);
    }

    /// <summary>
    /// Saying no is a write too: unticking the box on a reinstall has to clear a yes that is
    /// already there, not skip the write because the answer was negative.
    /// </summary>
    [Fact]
    public void Declining_clears_an_answer_that_was_already_yes()
    {
        Directory.CreateDirectory(root);
        UpdateCheckPolicyWriter.Apply(root, enabled: true);

        UpdateCheckPolicyWriter.Apply(root, enabled: false);

        Assert.False(new UpdateCheckPolicyStore(root).Read().Enabled);
    }

    [Fact]
    public void An_interval_somebody_tuned_survives_the_answer_changing()
    {
        Directory.CreateDirectory(root);
        new UpdateCheckPolicyStore(root).Write(
            UpdateCheckPolicy.Default with { IntervalHours = 6 });

        UpdateCheckPolicyWriter.Apply(root, enabled: true);

        var policy = new UpdateCheckPolicyStore(root).Read();
        Assert.True(policy.Enabled);
        Assert.Equal(6, policy.IntervalHours);
    }
}
