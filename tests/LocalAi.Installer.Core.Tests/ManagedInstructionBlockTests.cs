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
    [InlineData("ETA")]
    // Report what the tool returns. The trap this replaced was asking for a figure at the
    // start of a build, when the phase is not counted at all — which leaves inventing one as
    // the only way to comply.
    [InlineData("processed and total chunks")]
    [InlineData("not counted in the phases before and after")]
    // Hidden indexing is indistinguishable from a hung machine, and the person watching has
    // no other way to tell the two apart.
    [InlineData("never filter the indexer")]
    // The index covers commits. Edits still in the working tree need an overlay that nothing
    // builds on its own, and without this the refusal reads as a broken tool rather than a
    // missing step — which is how a text search gets reached for instead.
    [InlineData("Uncommitted work is not in the index yet")]
    // Named because a sync aimed at the repository root while you are editing a worktree
    // builds an overlay for somewhere else, and the search still refuses.
    [InlineData("the worktree you are editing")]
    // All four hooks, so nobody syncs by hand after a rebase.
    [InlineData("commit, checkout, merge")]
    [InlineData("rewrite")]
    // Leaving the local tool is a decision with a reason, not a silent fallback.
    [InlineData("ask before switching")]
    // The report is a shape, not a sentiment: a local call reported vaguely cannot be told
    // from one that never happened.
    [InlineData("Saved roughly")]
    [InlineData("four characters per token")]
    // The first thing to check when the index lags HEAD, and the one people never think of.
    [InlineData("core.hooksPath")]
    [InlineData("git rev-parse --git-path hooks")]
    [InlineData("localai repo status --root")]
    [InlineData("localai sync --root")]
    [InlineData("localai hooks install --root")]
    [InlineData("Connected is not ready")]
    public void The_block_states_every_rule_the_installation_depends_on(string rule) =>
        // Whitespace-normalised: the block is wrapped to a column, so a required phrase can
        // fall across a line break without the requirement having changed at all.
        Assert.Contains(
            rule,
            string.Join(" ", ManagedInstructionBlock.Block.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)),
            StringComparison.Ordinal);

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

    /// <summary>
    /// The user's own guidance outranks this block — except where it cannot.
    ///
    /// The first attempt at this carve-out ended "if one genuinely needs relaxing, say so and
    /// ask", which applied to both named rules. For the untrusted-content boundary that is a
    /// pre-blessed shape for the exact request an injection wants to make, and it contradicted
    /// the absolute prohibition further down the same block. The boundary has no escape; the
    /// transport rule has exactly one, and it is a policy rather than a line of guidance.
    /// </summary>
    [Fact]
    public void The_precedence_rule_does_not_reach_the_boundary()
    {
        var block = ManagedInstructionBlock.Block;
        Assert.Contains("theirs wins", block, StringComparison.Ordinal);

        // Bounded to the paragraph. Searching to the end of the block found "broker" and
        // "untrusted-content" in unrelated sections, so the assertion passed with the
        // carve-out deleted.
        var start = block.IndexOf("not a preference", StringComparison.Ordinal);
        Assert.True(start > 0, "the block must name what the user's guidance cannot override");
        var end = block.IndexOf("\n\n", start, StringComparison.Ordinal);
        var carveOut = end > start ? block[start..end] : block[start..];

        Assert.Contains("untrusted-content", carveOut, StringComparison.Ordinal);
        Assert.Contains("no way to ask", carveOut, StringComparison.Ordinal);
        // The boundary paragraph must not offer the escape the transport rule has.
        Assert.DoesNotContain("say so and ask", carveOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transport rule keeps its one documented way out — a policy, not a preference — so
    /// that tightening the boundary does not quietly forbid something the product supports.
    /// </summary>
    [Fact]
    public void The_transport_rule_keeps_the_one_escape_it_has()
    {
        var block = ManagedInstructionBlock.Block;
        var transport = block[block.IndexOf("The transport rule", StringComparison.Ordinal)..];

        Assert.Contains("policy", transport[..400], StringComparison.Ordinal);
        Assert.Contains("never by a line of guidance", transport[..400], StringComparison.Ordinal);
    }

    /// <summary>
    /// This text is prepended to every CLAUDE.md and AGENTS.md and read on every session, so a
    /// sentence that merely restates another one is paid for on every turn forever. The block
    /// grew 72% in one commit, most of it restatement, before anybody measured it.
    ///
    /// This catches copied sentences, which is the mistake that actually happened — a passage
    /// committed twice. It cannot catch restatement in different words; nothing automated can,
    /// and a pass here is not evidence that the block says each thing once.
    /// </summary>
    [Fact]
    public void The_block_says_nothing_twice()
    {
        var normalised = string.Join(" ", ManagedInstructionBlock.Block.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        var sentences = normalised
            .Split(['.', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 45)
            .ToArray();

        var repeated = sentences
            .GroupBy(sentence => sentence, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(repeated);
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
