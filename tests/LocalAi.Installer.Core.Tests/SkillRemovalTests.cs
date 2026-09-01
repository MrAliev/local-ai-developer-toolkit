using System.Text;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Disconnecting a client has always meant "leave nothing of ours behind". The instructions
/// used to be one block inside a file the user owns, so removal was a text edit. The skill is a
/// whole file this installer creates, and a plan that can only ever write leaves it there —
/// pointing at a launcher the uninstall just removed.
///
/// An edited file is a different matter: destroying somebody's edit is worse than leaving a
/// file they can be told about.
/// </summary>
public sealed class SkillRemovalTests : IDisposable
{
    private readonly string home = Path.Combine(
        Path.GetTempPath(),
        "localai-skill-" + Guid.NewGuid().ToString("N"));

    private readonly DateTimeOffset now = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private string SkillPath => Path.Combine(home, ManagedInstructionBlock.SkillRelativePath);

    [Fact]
    public void Disconnecting_plans_the_skill_for_deletion_when_it_is_ours()
    {
        WriteSkill(ManagedInstructionBlock.SkillFile());

        var plan = Adapter().PreviewRemoval();
        var skill = plan.Files.Single(file =>
            file.Path.Equals(SkillPath, StringComparison.OrdinalIgnoreCase));

        Assert.True(skill.Deletes, "a file we wrote and nobody touched is ours to remove");
        Assert.Empty(skill.AfterBytes);
    }

    /// <summary>
    /// An empty write is not a deletion: it leaves a zero-byte file, which is the state the
    /// plan model produced before it could express this at all.
    /// </summary>
    [Fact]
    public async Task Applying_the_removal_takes_the_file_off_disk()
    {
        WriteSkill(ManagedInstructionBlock.SkillFile());

        await AgentConfigurationFileOperations.ApplyAsync(
            Adapter().PreviewRemoval(),
            File.ReadAllBytes,
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(SkillPath));
    }

    [Fact]
    public void An_edited_skill_is_left_alone()
    {
        WriteSkill(ManagedInstructionBlock.SkillFile() + "\nMy own note.\n");

        var plan = Adapter().PreviewRemoval();

        Assert.DoesNotContain(
            plan.Files,
            file => file.Path.Equals(SkillPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_machine_without_the_skill_plans_nothing_for_it()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", "CLAUDE.md"), "Mine.\n", Encoding.UTF8);

        var plan = Adapter().PreviewRemoval();

        Assert.DoesNotContain(
            plan.Files,
            file => file.Path.Equals(SkillPath, StringComparison.OrdinalIgnoreCase));
    }

    private void WriteSkill(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SkillPath)!);
        File.WriteAllText(SkillPath, content, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(
            Path.Combine(home, ".claude", "CLAUDE.md"),
            ManagedInstructionBlock.Upsert("Mine.\n").Content,
            new UTF8Encoding(false));
    }

    private ClaudeConfigurationAdapter Adapter() =>
        new(home, @"C:\LocalAi\bin", TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(home))
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
