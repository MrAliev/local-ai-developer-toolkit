namespace LocalAi.Contracts;

/// <summary>
/// How strictly a model must live in video memory before the broker will serve it.
///
/// The default exists because a model that spills to system memory does not fail — it just
/// becomes several times slower, and nothing about that announces itself. The report line a
/// local tool prints is made to say it, beside the model; the tools that print no such line
/// say nothing. Relaxing this is a deliberate trade for machines without a discrete adapter,
/// not a performance tweak.
/// </summary>
public enum ModelResidencyPolicy
{
    /// <summary>
    /// The model must be fully resident in video memory. Anything else is refused.
    /// </summary>
    RequireFullVram = 0,

    /// <summary>
    /// Part of the model may spill to system memory, as long as some of it is on the GPU.
    /// An answer produced this way is labelled as degraded where the model is named.
    /// </summary>
    AllowPartialOffload = 1,

    /// <summary>
    /// The model may run entirely on the CPU. Usable on integrated graphics and on machines
    /// with no usable adapter at all; expect a large slowdown. An answer produced this way is
    /// labelled as degraded where the model is named.
    /// </summary>
    AllowCpu = 2,
}

public static class ModelResidencyPolicyExtensions
{
    /// <summary>
    /// Human-readable warning for a load that did not reach full residency, or null when the
    /// model is fully resident.
    ///
    /// English prose, so it goes to the preflight output as it stands. The report line a local
    /// tool prints is rendered by the client in its own language, and takes the fact rather
    /// than the sentence — ModelResidencyProof.Shortfall.
    /// </summary>
    public static string? DescribeDegradation(
        this ModelResidencyPolicy policy,
        long sizeBytes,
        long sizeVramBytes)
    {
        // >= rather than ==: Ollama has reported more in video memory than the model's own
        // size, and this used to answer "only 103% of the model is in video memory". Nothing
        // read it then; the preflight output returns it now, so the absurdity would ship.
        if (sizeBytes <= 0 || sizeVramBytes >= sizeBytes)
        {
            return null;
        }

        if (sizeVramBytes <= 0)
        {
            return "running on CPU: the model is not in video memory at all, " +
                "so responses are far slower than a fully resident load.";
        }

        var residentPercent = (int)(sizeVramBytes * 100L / sizeBytes);
        return $"partially offloaded: only {residentPercent}% of the model is in video " +
            "memory, so responses are slower than a fully resident load.";
    }
}
