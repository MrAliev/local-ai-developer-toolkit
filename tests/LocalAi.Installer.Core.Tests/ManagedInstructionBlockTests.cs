using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

public sealed class ManagedInstructionBlockTests
{
    [Fact]
    public void Adds_unique_managed_block_without_changing_existing_text()
    {
        var existing = "Keep this guidance.\r\n";

        var result = ManagedInstructionBlock.Upsert(existing);

        Assert.True(result.Changed);
        Assert.StartsWith(existing, result.Content, StringComparison.Ordinal);
        Assert.Contains(ManagedInstructionBlock.BeginMarker, result.Content, StringComparison.Ordinal);
        Assert.Contains(ManagedInstructionBlock.EndMarker, result.Content, StringComparison.Ordinal);
        Assert.Contains("Use only the shared LocalAi FIFO broker", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Ollama directly", existing, StringComparison.Ordinal);
    }

    [Fact]
    public void Replaces_existing_managed_block_and_reports_no_change_when_current()
    {
        var stale = "Header\n" +
            ManagedInstructionBlock.BeginMarker + "\nold\n" +
            ManagedInstructionBlock.EndMarker + "\nFooter\n";

        var updated = ManagedInstructionBlock.Upsert(stale);
        var unchanged = ManagedInstructionBlock.Upsert(updated.Content);

        Assert.True(updated.Changed);
        Assert.False(unchanged.Changed);
        Assert.Equal(updated.Content, unchanged.Content);
        Assert.Contains("Header\n", updated.Content, StringComparison.Ordinal);
        Assert.Contains("Footer\n", updated.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\nold\n", updated.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The block is the only thing that tells an assistant when to reach for the tools this
    /// installer just registered. Transport rules alone left it grepping the repository and
    /// sending screenshots to the cloud, so each routing decision is asserted by name.
    /// </summary>
    [Theory]
    [InlineData("search_code")]
    [InlineData("read_image")]
    [InlineData("triage_log")]
    [InlineData("ask_local")]
    [InlineData("index_status")]
    [InlineData("Use only the shared LocalAi FIFO broker")]
    [InlineData("Never access Ollama")]
    [InlineData("full-VRAM, zero-offload")]
    [InlineData("estimated cloud tokens avoided")]
    [InlineData("exact `index_unload` tool name")]
    [InlineData("processed, total and remaining chunks")]
    [InlineData("current ETA")]
    // Indexing is opt-in per repository, so an assistant that does not know how to connect
    // one is limited to whatever was set up before it arrived.
    // With --root, because the block is read by agents that are not standing in the repository
    // they are asking about, and the bare form answers about wherever the process happens to be.
    [InlineData("localai repo status --root")]
    [InlineData("localai sync --root")]
    [InlineData("localai hooks install --root")]
    [InlineData("INITIALIZING")]
    public void The_block_states_every_rule_the_installation_depends_on(string rule) =>
        Assert.Contains(rule, ManagedInstructionBlock.Block, StringComparison.Ordinal);

    [Fact]
    public void The_block_is_written_with_one_line_ending_convention()
    {
        var block = ManagedInstructionBlock.Block;

        // A raw literal keeps whatever the source file uses, and a mixed-ending block lands
        // in the middle of a user's own file.
        Assert.Equal(block.ReplaceLineEndings(), block);
        Assert.StartsWith(ManagedInstructionBlock.BeginMarker, block, StringComparison.Ordinal);
        Assert.EndsWith(ManagedInstructionBlock.EndMarker, block, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\n<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->\nb\n<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->\nc\n<!-- END LOCALAI MANAGED INSTRUCTIONS -->")]
    [InlineData("a\n<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->\nb")]
    [InlineData("a\n<!-- END LOCALAI MANAGED INSTRUCTIONS -->\nb")]
    public void Duplicate_or_malformed_markers_are_rejected(string content)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ManagedInstructionBlock.Upsert(content));

        Assert.Contains("managed instruction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An uninstall gives the file back as it was found: what upsert appended, remove takes
    /// away, down to the line ending it inserted after the block.
    /// </summary>
    [Theory]
    [InlineData("Keep this guidance.\r\n")]
    [InlineData("")]
    [InlineData("Header\n\nA paragraph.\n\nAnother.\n")]
    public void Removing_the_block_undoes_adding_it(string original)
    {
        var withBlock = ManagedInstructionBlock.Upsert(original);

        var removed = ManagedInstructionBlock.Remove(withBlock.Content);

        Assert.True(removed.Changed);
        Assert.Equal(original, removed.Content);
    }

    /// <summary>
    /// The one asymmetry, and it is deliberate. Appending to a file that did not end in a
    /// newline required inserting one to separate the block from the last line; removal leaves
    /// it, because a line ending immediately before the block is indistinguishable from one the
    /// person typed — and returning a file one byte shorter than they wrote it is the worse of
    /// the two mistakes. Every character they wrote survives either way.
    /// </summary>
    [Fact]
    public void A_file_that_ended_mid_line_keeps_the_separator_the_block_needed()
    {
        const string original = "Keep this guidance without a trailing newline.";

        var removed = ManagedInstructionBlock.Remove(
            ManagedInstructionBlock.Upsert(original).Content);

        Assert.Equal(original + Environment.NewLine, removed.Content);
        Assert.StartsWith(original, removed.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_on_both_sides_of_the_block_survives()
    {
        var content =
            "# Mine\n\nBefore.\n" +
            ManagedInstructionBlock.Block + "\n" +
            "After.\n";

        var removed = ManagedInstructionBlock.Remove(content);

        Assert.Equal("# Mine\n\nBefore.\nAfter.\n", removed.Content);
    }

    [Fact]
    public void A_file_that_never_carried_the_block_is_left_exactly_as_it_is()
    {
        const string content = "Nothing of ours here.\n";

        var removed = ManagedInstructionBlock.Remove(content);

        Assert.False(removed.Changed);
        Assert.Equal(content, removed.Content);
    }

    [Theory]
    [InlineData("a\n<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->\nb\n<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->\nc\n<!-- END LOCALAI MANAGED INSTRUCTIONS -->")]
    [InlineData("a\n<!-- BEGIN LOCALAI MANAGED INSTRUCTIONS -->\nb")]
    [InlineData("a\n<!-- END LOCALAI MANAGED INSTRUCTIONS -->\nb")]
    public void Removal_refuses_the_same_malformed_markers_the_upsert_does(string content)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ManagedInstructionBlock.Remove(content));

        Assert.Contains("managed instruction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
