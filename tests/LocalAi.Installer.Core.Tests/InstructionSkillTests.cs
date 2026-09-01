using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The block used to be one text in every client's configuration file. It is three now: a core
/// that has to be in context before any decision is taken, a skill body Claude loads when it
/// needs the reference, and the two concatenated for Codex, which has no import mechanism of
/// any kind and must carry everything inline.
///
/// The split is by when a sentence is read, not by length. A rule that fires at the moment a
/// tool refuses cannot live in a skill: consulting the skill is itself a decision that would
/// have to be taken first.
/// </summary>
public sealed class InstructionSkillTests
{
    private static string Flat(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The rule the user made global: never go quiet when a local tool refuses. It fires at the
    /// refusal, so it is core — and it has to carry the three ways forward, or "say something"
    /// degrades into an apology with no decision attached.
    /// </summary>
    [Theory]
    [InlineData("quote the refusal verbatim")]
    [InlineData("index it")]
    [InlineData("fix the cause")]
    [InlineData("work with cloud tools")]
    [InlineData("paid for in cloud tokens")]
    public void The_core_says_what_to_do_when_a_local_tool_refuses(string phrase) =>
        Assert.Contains(phrase, Flat(ManagedInstructionBlock.Block), StringComparison.Ordinal);

    /// <summary>
    /// What moved out. Each of these is read after a decision is taken — how to build an
    /// overlay, where hooks live, what the residency policy is called — so none of them needs
    /// to sit in context on every session.
    /// </summary>
    [Theory]
    [InlineData("core.hooksPath")]
    [InlineData("git rev-parse --git-path hooks")]
    [InlineData("localai sync --root <repository>")]
    [InlineData("localai hooks install --root")]
    [InlineData("passing the worktree as its root")]
    [InlineData("Full-VRAM, zero-offload validation is the default")]
    [InlineData("processed and total chunks")]
    [InlineData("not counted in this phase")]
    public void The_skill_holds_what_is_read_after_the_decision(string rule) =>
        Assert.Contains(rule, Flat(ManagedInstructionBlock.SkillBody), StringComparison.Ordinal);

    /// <summary>
    /// Codex gets everything inline because it has no import mechanism, so it must not be told
    /// to invoke something that does not exist there.
    /// </summary>
    [Fact]
    public void The_codex_text_never_tells_it_to_invoke_a_skill()
    {
        var codex = Flat(ManagedInstructionBlock.CodexBlock);

        Assert.DoesNotContain("Invoke it before", codex, StringComparison.Ordinal);
        Assert.DoesNotContain("the skill says how", codex, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the skill describes", codex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And carries the reference material anyway — that is the whole point of concatenating it.
    /// </summary>
    [Theory]
    [InlineData("core.hooksPath")]
    [InlineData("quote the refusal verbatim")]
    public void The_codex_text_carries_both_halves(string phrase) =>
        Assert.Contains(phrase, Flat(ManagedInstructionBlock.CodexBlock), StringComparison.Ordinal);

    /// <summary>
    /// The skill's description is what sits in context permanently, so it is the one piece of
    /// the skill that is paid for on every session whether or not it is ever invoked. It has to
    /// say when to reach for the skill, or it is a cost with no benefit.
    /// </summary>
    [Fact]
    public void The_skill_description_says_when_to_reach_for_it()
    {
        var description = ManagedInstructionBlock.SkillDescription;

        Assert.Contains("LocalAi", description, StringComparison.Ordinal);
        Assert.InRange(description.Length, 120, 1_536);
    }

    /// <summary>
    /// Splitting a text is how the same sentence ends up in both halves. Codex reads them
    /// concatenated, so a repeat there is a repeat in a shipped document.
    /// </summary>
    [Fact]
    public void Nothing_is_said_twice_across_the_two_halves()
    {
        var sentences = ManagedInstructionBlock.CodexBlock
            .Split(['.', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(sentence => Flat(sentence))
            .Where(sentence => sentence.Length > 45)
            .ToArray();

        var repeated = sentences
            .GroupBy(sentence => sentence, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(repeated);
    }

    /// <summary>
    /// The core is always resident, so its size is a recurring cost on every session. It was
    /// the whole block before; splitting is only worth doing if the core is materially smaller.
    /// </summary>
    [Fact]
    public void The_core_is_materially_smaller_than_the_block_it_replaced()
    {
        Assert.InRange(ManagedInstructionBlock.Block.Length, 1_000, 6_500);
    }
}
