namespace LocalAi.Contracts;

/// <summary>
/// How strictly a model must live in video memory before the broker will serve it.
///
/// The default exists because a model that silently spills to system memory does not fail —
/// it just becomes several times slower, and nothing in the answer says so. Relaxing this is
/// a deliberate trade for machines without a discrete adapter, not a performance tweak.
/// </summary>
public enum ModelResidencyPolicy
{
    /// <summary>
    /// The model must be fully resident in video memory. Anything else is refused.
    /// </summary>
    RequireFullVram = 0,

    /// <summary>
    /// Part of the model may spill to system memory, as long as some of it is on the GPU.
    /// Every answer produced this way carries a degradation warning.
    /// </summary>
    AllowPartialOffload = 1,

    /// <summary>
    /// The model may run entirely on the CPU. Usable on integrated graphics and on machines
    /// with no usable adapter at all; expect a large slowdown.
    /// </summary>
    AllowCpu = 2,
}

public static class ModelResidencyPolicyExtensions
{
    /// <summary>
    /// Human-readable warning for a load that did not reach full residency, or null when the
    /// model is fully resident. Callers are expected to surface this, not to swallow it.
    /// </summary>
    public static string? DescribeDegradation(
        this ModelResidencyPolicy policy,
        long sizeBytes,
        long sizeVramBytes)
    {
        if (sizeBytes <= 0 || sizeVramBytes == sizeBytes)
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
