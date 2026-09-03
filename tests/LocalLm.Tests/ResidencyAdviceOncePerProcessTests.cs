using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

/// <summary>
/// The mark beside the model goes on every degraded answer — it is four words inside a line
/// that is printed anyway. The advice on how to undo it does not: a full sentence on every
/// call is how a line stops being read.
///
/// Once per process is what "once per session" means here. The MCP server lives as long as the
/// session that started it, so an assistant sees it once; a CLI invocation is one thing a
/// person looks at, so they see it once too. A timestamp in the runtime directory would have
/// meant the second session on a shared machine never sees it, which is the failure #277
/// describes.
/// </summary>
public sealed class ResidencyAdviceOncePerProcessTests
{
    [Fact]
    public void The_advice_appears_on_the_first_degraded_answer_and_not_the_second()
    {
        var state = new ResidencyAdvice();

        Assert.NotNull(state.AdviceFor(ResidencyShortfall.PartialOffload));
        Assert.Null(state.AdviceFor(ResidencyShortfall.PartialOffload));
    }

    /// <summary>A different kind of shortfall is a different fact, so it is said again.</summary>
    [Fact]
    public void A_shortfall_of_another_kind_is_said_again()
    {
        var state = new ResidencyAdvice();

        Assert.NotNull(state.AdviceFor(ResidencyShortfall.PartialOffload));
        Assert.NotNull(state.AdviceFor(ResidencyShortfall.Cpu));
        Assert.Null(state.AdviceFor(ResidencyShortfall.Cpu));
    }

    /// <summary>
    /// "Once per kind" has to mean once, not once since the kind last changed. Under AllowCpu
    /// the two can alternate — profiles route to different models, and a large one entirely on
    /// the processor sits beside a smaller one partly offloaded — so a single "last kind" field
    /// prints the sentence on every call, which is the wallpaper this class exists to avoid.
    /// </summary>
    [Fact]
    public void Alternating_kinds_do_not_start_the_advice_over()
    {
        var state = new ResidencyAdvice();

        Assert.NotNull(state.AdviceFor(ResidencyShortfall.PartialOffload));
        Assert.NotNull(state.AdviceFor(ResidencyShortfall.Cpu));
        Assert.Null(state.AdviceFor(ResidencyShortfall.PartialOffload));
        Assert.Null(state.AdviceFor(ResidencyShortfall.Cpu));
    }

    [Fact]
    public void A_healthy_answer_carries_no_advice_and_does_not_use_up_the_first_one()
    {
        var state = new ResidencyAdvice();

        Assert.Null(state.AdviceFor(ResidencyShortfall.None));
        Assert.NotNull(state.AdviceFor(ResidencyShortfall.Cpu));
    }

    [Fact]
    public void The_advice_names_the_way_back_and_that_it_needs_a_restart()
    {
        var advice = new ResidencyAdvice().AdviceFor(ResidencyShortfall.Cpu);

        Assert.Contains("localai policy set --residency RequireFullVram", advice);
        Assert.Contains("after a restart", advice, StringComparison.OrdinalIgnoreCase);
    }
}
