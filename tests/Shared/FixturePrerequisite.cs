namespace LocalAi.TestSupport;

internal enum PrerequisiteOutcome
{
    Satisfied,
    Skip,
    Fail
}

/// <summary>
/// One place to decide what a missing prerequisite means.
///
/// Fixtures that drive a real external tool have to do something when the tool is absent, and
/// skipping is right on a developer machine that never installed it. It is wrong everywhere the
/// suite is supposed to be authoritative: a skip reads as coverage in every report, and three
/// releases in a row shipped defects that a fixture would have caught had it ever run — the
/// scip-python failure in 0.1.31, the executable resolver in 0.1.32, and live TypeScript
/// navigation in 0.1.33, each behind a fixture that quietly skipped.
///
/// So the same call means both things, and the environment decides which. Set
/// <c>LOCALAI_STRICT_FIXTURES=1</c> — CI does — and a missing prerequisite fails the run with a
/// message naming what to install. Leave it unset and it skips as before.
///
/// This deliberately does not turn failures into skips. A prerequisite that is present and then
/// does not work keeps failing in both modes; that distinction is the whole point.
/// </summary>
internal static class FixturePrerequisite
{
    public const string StrictVariable = "LOCALAI_STRICT_FIXTURES";

    /// <summary>
    /// Prerequisites this environment is known not to be able to supply, named one by one.
    ///
    /// Some tools cannot run where the suite runs and it is nobody's fault here: scip-python
    /// 0.6.6 crashes on load under Windows on <c>new RegExp(path.sep)</c>, which is an invalid
    /// pattern, and it only reaches that code when it has to build a virtual environment — so it
    /// works on a developer machine with an interpreter it accepts and dies on a clean runner.
    ///
    /// The exception is declared rather than assumed. A named entry keeps strictness meaning
    /// something for every other tool, appears in the run output, and can be grepped for and
    /// removed when the upstream bug is fixed. Quietly skipping instead would restore exactly the
    /// blindness this class exists to remove.
    /// </summary>
    public const string ExceptVariable = "LOCALAI_STRICT_FIXTURES_EXCEPT";

    public static bool IsStrict =>
        Environment.GetEnvironmentVariable(StrictVariable) is "1" or "true" or "TRUE";

    public static IReadOnlyCollection<string> Excused =>
        (Environment.GetEnvironmentVariable(ExceptVariable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Whether <paramref name="what"/> was excused. Substring rather than equality so a fixture
    /// can describe its prerequisite in a sentence — "a usable tsserver.js beside
    /// typescript-language-server" — while the excuse names only the tool.
    /// </summary>
    public static bool IsExcused(string what, IReadOnlyCollection<string> excused) =>
        excused.Any(entry => what.Contains(entry, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Continues when <paramref name="isPresent"/> holds. Otherwise fails in strict mode and
    /// skips outside it, saying what is missing and how to supply it either way.
    /// </summary>
    public static void Require(
        bool isPresent,
        string what,
        string howToSupplyIt,
        bool? strict = null)
    {
        var outcome = Decide(
            isPresent,
            (strict ?? IsStrict) && !IsExcused(what, Excused));
        if (outcome == PrerequisiteOutcome.Satisfied)
        {
            return;
        }

        var reason = Explain(what, howToSupplyIt, outcome);
        if (outcome == PrerequisiteOutcome.Fail)
        {
            Assert.Fail(reason);
        }

        Assert.Skip(reason);
    }

    /// <summary>
    /// The decision, separated from acting on it, so it can be tested.
    ///
    /// The first version of this had no such split, and its test for the skip path called
    /// <see cref="Assert.Skip"/> through <c>Record.Exception</c> — which skipped the test itself.
    /// A test that skips whenever the thing it checks happens is exactly the failure this whole
    /// mechanism exists to remove, and it took writing one to see it.
    /// </summary>
    public static PrerequisiteOutcome Decide(bool isPresent, bool strict) =>
        isPresent
            ? PrerequisiteOutcome.Satisfied
            : strict
                ? PrerequisiteOutcome.Fail
                : PrerequisiteOutcome.Skip;

    public static string Explain(
        string what,
        string howToSupplyIt,
        PrerequisiteOutcome outcome)
    {
        var reason = $"{what} is not available. {howToSupplyIt}";
        return outcome == PrerequisiteOutcome.Fail
            ? $"{reason} This run is strict ({StrictVariable} is set), so a fixture that " +
              "cannot exercise the product is a failure rather than a skip."
            : reason;
    }

    /// <summary>
    /// The same check for a value the caller then uses, returning it non-null.
    ///
    /// <see cref="Assert.Skip"/> is annotated as never returning, so the compiler used to know a
    /// candidate was non-null after the guard. Routing the decision through a helper loses that,
    /// and the alternative is a null-forgiving operator at every use — which suppresses exactly
    /// the warning worth keeping elsewhere. Returning the value keeps the knowledge.
    /// </summary>
    public static string RequireText(
        string? candidate,
        string what,
        string howToSupplyIt,
        bool? strict = null)
    {
        Require(!string.IsNullOrWhiteSpace(candidate), what, howToSupplyIt, strict);
        return candidate!;
    }
}
