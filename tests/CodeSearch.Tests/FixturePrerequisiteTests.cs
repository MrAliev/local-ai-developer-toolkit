using LocalAi.TestSupport;

namespace CodeSearch.Tests;

/// <summary>
/// The mechanism that decides what a missing external tool means. Worth its own tests because
/// getting it wrong is silent in the direction that matters: a run that skips everything reports
/// the same green as a run that exercised everything.
/// </summary>
public sealed class FixturePrerequisiteTests
{
    [Fact]
    public void A_present_prerequisite_lets_the_fixture_continue()
    {
        var failure = Record.Exception(
            () => FixturePrerequisite.Require(true, "a tool", "Install it.", strict: true));

        Assert.Null(failure);
    }

    /// <summary>
    /// The decision is asserted directly rather than by calling the skip and watching what
    /// happens. The first version of this test did the latter and skipped itself — proving
    /// nothing, in precisely the way this mechanism exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(true, false, PrerequisiteOutcome.Satisfied)]
    [InlineData(true, true, PrerequisiteOutcome.Satisfied)]
    [InlineData(false, false, PrerequisiteOutcome.Skip)]
    [InlineData(false, true, PrerequisiteOutcome.Fail)]
    internal void The_decision_depends_on_presence_and_strictness(
        bool isPresent,
        bool strict,
        PrerequisiteOutcome expected)
    {
        Assert.Equal(expected, FixturePrerequisite.Decide(isPresent, strict));
    }

    /// <summary>
    /// An excuse is matched by substring so a fixture can describe its prerequisite in a
    /// sentence while the excuse names only the tool.
    /// </summary>
    [Theory]
    [InlineData("scip-python", new[] { "scip-python" }, true)]
    [InlineData("scip-python indexing a project", new[] { "scip-python" }, true)]
    [InlineData("scip-typescript", new[] { "scip-python" }, false)]
    [InlineData("scip-python", new string[0], false)]
    public void An_excused_prerequisite_is_recognised_by_name(
        string what,
        string[] excused,
        bool expected)
    {
        Assert.Equal(expected, FixturePrerequisite.IsExcused(what, excused));
    }

    [Fact]
    public void A_skip_reason_names_the_tool_without_mentioning_strictness()
    {
        var reason = FixturePrerequisite.Explain(
            "scip-typescript",
            "Install it.",
            PrerequisiteOutcome.Skip);

        Assert.Contains("scip-typescript", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            FixturePrerequisite.StrictVariable,
            reason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// In strict mode it fails, and the message has to name the tool and how to get it — the
    /// person reading a red CI run is not the person who wrote the fixture.
    /// </summary>
    [Fact]
    public void A_missing_prerequisite_fails_the_run_when_strict()
    {
        var outcome = Record.Exception(
            () => FixturePrerequisite.Require(
                false,
                "scip-typescript",
                "Install it with npm.",
                strict: true));

        Assert.NotNull(outcome);
        Assert.DoesNotContain("Skip", outcome.GetType().Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scip-typescript", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Install it with npm.", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(
            FixturePrerequisite.StrictVariable,
            outcome.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_value_overload_returns_what_the_fixture_then_uses()
    {
        var path = FixturePrerequisite.RequireText(
            @"C:\tools\scip-typescript.cmd",
            "scip-typescript",
            "Install it.",
            strict: true);

        Assert.Equal(@"C:\tools\scip-typescript.cmd", path);
    }

    /// <summary>
    /// Whitespace is absence. An environment variable set to an empty string is a configuration
    /// mistake, and treating it as a path produces a failure far from its cause.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_counts_as_missing(string? candidate)
    {
        var outcome = Record.Exception(
            () => FixturePrerequisite.RequireText(
                candidate,
                "a tool",
                "Install it.",
                strict: true));

        Assert.NotNull(outcome);
    }
}
