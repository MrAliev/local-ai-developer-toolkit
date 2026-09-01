using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Mcp;

namespace LocalLm.Tests;

/// <summary>
/// The advice has to be reachable from what a tool actually returns. A helper nothing calls is
/// how `DegradationWarning` came to be written and never read in the first place (#277), and
/// the same shape would be no better for its replacement.
/// </summary>
public sealed class ReportCarriesTheAdviceTests
{
    [Fact]
    public void A_degraded_answer_carries_the_notice_the_advice_and_the_answer()
    {
        var advice = new ResidencyAdvice();

        var report = LocalLmTools.Report(
            "🔧 Локально: m (целиком на процессоре — ответы намного медленнее). …",
            ResidencyShortfall.Cpu,
            "answer",
            "ask_local",
            advice);

        Assert.Contains("🔧 Локально:", report);
        Assert.Contains("localai policy set --residency RequireFullVram", report);
        Assert.Contains("answer", report);
    }

    [Fact]
    public void A_healthy_answer_reads_exactly_as_it_did()
    {
        var report = LocalLmTools.Report(
            "🔧 Локально: m. …",
            ResidencyShortfall.None,
            "answer",
            "ask_local",
            new ResidencyAdvice());

        Assert.DoesNotContain("policy set", report);
        Assert.StartsWith("🔧 Локально: m. …", report);
    }

    /// <summary>The second degraded answer keeps the mark and drops the sentence.</summary>
    [Fact]
    public void The_advice_is_said_once_while_the_notice_stays()
    {
        var advice = new ResidencyAdvice();
        var notice = "🔧 Локально: m (целиком на процессоре — ответы намного медленнее). …";

        LocalLmTools.Report(notice, ResidencyShortfall.Cpu, "a", "ask_local", advice);
        var second = LocalLmTools.Report(notice, ResidencyShortfall.Cpu, "b", "ask_local", advice);

        Assert.DoesNotContain("policy set", second);
        Assert.Contains("целиком на процессоре", second);
    }
}
