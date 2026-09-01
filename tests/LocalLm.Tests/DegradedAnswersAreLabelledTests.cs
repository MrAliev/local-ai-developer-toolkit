using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

/// <summary>
/// Relaxing residency lets a model spill out of video memory. The design said such answers are
/// labelled so nobody mistakes a degraded one for a healthy one; the label was never wired up,
/// so `DegradationWarning` was written and read by nobody (#277).
///
/// Someone who relaxed the policy weeks ago, or anyone else sharing that machine, had no signal
/// at all — while three shipped surfaces promised there would be one.
/// </summary>
public sealed class DegradedAnswersAreLabelledTests
{
    /// <summary>
    /// The percentage is the point: it says how much of the model reached video memory, which
    /// is what turns a warning into information.
    /// </summary>
    [Fact]
    public void A_partly_offloaded_model_is_named_as_one_beside_the_model()
    {
        var notice = Result(ResidencyShortfall.PartialOffload, residentPercent: 42).Notice;

        Assert.Contains("qwen3-coder:30b (в видеопамяти 42% модели — ответы медленнее)", notice);
    }

    /// <summary>Running on the CPU is a different fact and reads differently.</summary>
    [Fact]
    public void A_model_on_the_processor_is_named_as_one()
    {
        var notice = Result(ResidencyShortfall.Cpu, residentPercent: 0).Notice;

        Assert.Contains(
            "qwen3-coder:30b (целиком на процессоре — ответы намного медленнее)",
            notice);
    }

    /// <summary>
    /// A healthy answer carries no mark. A parenthesis on every line is how a line stops being
    /// read, and the mark has to mean something when it appears.
    /// </summary>
    [Fact]
    public void A_fully_resident_model_is_not_marked()
    {
        var notice = Result(ResidencyShortfall.None, residentPercent: 100).Notice;

        Assert.DoesNotContain("(", notice);
    }

    /// <summary>A receipt from before this shipped, or from a path that does not route.</summary>
    [Fact]
    public void A_receipt_without_routing_is_not_marked()
    {
        var notice = Result(null, null).Notice;

        Assert.DoesNotContain("видеопамяти", notice);
        Assert.DoesNotContain("процессоре", notice);
    }

    /// <summary>
    /// translate_local reports through its own line. Left out, it would be the one local tool
    /// whose answers are never marked — and the mark is worth exactly as much as its coverage.
    /// </summary>
    [Fact]
    public void A_translation_is_marked_the_same_way()
    {
        var source = Result(ResidencyShortfall.Cpu, residentPercent: 0);
        var translation = new LocalTranslationResult(
            "answer",
            SavedTokens: 30_000,
            LocalTokensProcessed: 1000,
            NetCloudContextTokensSaved: 29_000,
            Model: "qwen3-coder:30b",
            Validation: new TranslationValidationResult(true, "ok"),
            Receipt: source.Receipt);

        Assert.Contains(
            "qwen3-coder:30b (целиком на процессоре — ответы намного медленнее)",
            translation.Notice);
    }

    private static LocalResult Result(ResidencyShortfall? shortfall, int? residentPercent) =>
        new(
            "answer",
            30_000,
            "qwen3-coder:30b",
            "Прочитано 12 файлов",
            new LocalUsageReceipt(
                JobId: Guid.Empty,
                Tool: "ask_local",
                Operation: "Chat",
                Model: "qwen3-coder:30b",
                QueueDuration: TimeSpan.Zero,
                ExecutionDuration: TimeSpan.FromSeconds(8.2),
                InputCharacters: 0,
                EstimatedCloudTokensSaved: 30_000,
                RepositoryId: null,
                GenerationId: null,
                GitTree: null,
                Routing: shortfall is null
                    ? null
                    : new LocalRoutingReceipt(
                        TaskProfile: null,
                        SelectedModel: "qwen3-coder:30b",
                        ContextTokens: null,
                        WasCold: false,
                        UsedFallback: false,
                        ValidatorResult: null,
                        EstimatedGrossCloudTokensSaved: 30_000,
                        EstimatedVerificationTokens: 0,
                        EstimatedNetCloudTokensSaved: 30_000,
                        ResidencyShortfall: shortfall.Value,
                        VramResidentPercent: residentPercent)));
}
