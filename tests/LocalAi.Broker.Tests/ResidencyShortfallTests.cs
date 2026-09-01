using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The proof measures how much of a model reached video memory. Turning that into a fact the
/// client can render is what was missing: the proof carried an English sentence that nothing
/// read, so a degraded answer looked exactly like a healthy one (#277).
/// </summary>
public sealed class ResidencyShortfallTests
{
    [Fact]
    public void A_model_entirely_in_video_memory_has_no_shortfall()
    {
        var (shortfall, percent) = Proof(sizeBytes: 1000, sizeVramBytes: 1000).Shortfall();

        Assert.Equal(ResidencyShortfall.None, shortfall);
        Assert.Equal(100, percent);
    }

    [Fact]
    public void A_model_partly_in_system_memory_reports_the_share_that_arrived()
    {
        var (shortfall, percent) = Proof(sizeBytes: 1000, sizeVramBytes: 420).Shortfall();

        Assert.Equal(ResidencyShortfall.PartialOffload, shortfall);
        Assert.Equal(42, percent);
    }

    [Fact]
    public void A_model_with_nothing_in_video_memory_is_on_the_processor()
    {
        var (shortfall, percent) = Proof(sizeBytes: 1000, sizeVramBytes: 0).Shortfall();

        Assert.Equal(ResidencyShortfall.Cpu, shortfall);
        Assert.Equal(0, percent);
    }

    /// <summary>
    /// A size the runtime did not report is not a shortfall: answering PartialOffload on a
    /// missing measurement would mark healthy answers as degraded, which spends the mark's
    /// meaning on noise.
    /// </summary>
    [Fact]
    public void An_unmeasured_model_is_not_reported_as_degraded()
    {
        var (shortfall, percent) = Proof(sizeBytes: 0, sizeVramBytes: 0).Shortfall();

        Assert.Equal(ResidencyShortfall.None, shortfall);
        Assert.Null(percent);
    }

    /// <summary>
    /// A sliver in video memory is the processor running the model, whatever the arithmetic
    /// rounds to. Reported as PartialOffload it renders as "в видеопамяти 0% модели — ответы
    /// медленнее", which says "slower" about the case that is not slower but slowest.
    /// </summary>
    [Fact]
    public void A_sliver_in_video_memory_is_the_processor_running_it()
    {
        var (shortfall, percent) = Proof(sizeBytes: 20_000_000_000, sizeVramBytes: 40_000_000)
            .Shortfall();

        Assert.Equal(ResidencyShortfall.Cpu, shortfall);
        Assert.Equal(0, percent);
    }

    /// <summary>
    /// Ollama has reported more in video memory than the model's own size. It is nonsense, and
    /// the honest reading is that nothing is missing — not that 103% of the model arrived.
    /// </summary>
    [Fact]
    public void More_in_video_memory_than_the_model_is_not_a_shortfall()
    {
        var (shortfall, percent) = Proof(sizeBytes: 1000, sizeVramBytes: 1030).Shortfall();

        Assert.Equal(ResidencyShortfall.None, shortfall);
        Assert.Equal(100, percent);
    }

    private static ModelResidencyProof Proof(long sizeBytes, long sizeVramBytes) =>
        new(
            "qwen3-coder:30b",
            ContextTokens: 8192,
            SizeBytes: sizeBytes,
            SizeVramBytes: sizeVramBytes,
            FullyResident: sizeBytes > 0 && sizeBytes == sizeVramBytes,
            VerifiedAtUtc: DateTimeOffset.UnixEpoch);
}
