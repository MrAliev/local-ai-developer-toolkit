using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>
/// Says once, per kind of shortfall, how to get strict residency back.
///
/// The mark beside the model goes on every degraded answer — four words inside a line that is
/// printed anyway. This is a full sentence, and a full sentence on every call is how a line
/// stops being read. Once is enough to inform; twice is wallpaper.
///
/// The state is per process rather than a timestamp in the runtime directory, and that is a
/// decision rather than an economy: a file would mean the second session on a shared machine
/// never sees the advice, which is exactly the person #277 is about — someone who did not relax
/// the policy and has no way to know it is relaxed.
/// </summary>
public sealed class ResidencyAdvice
{
    private readonly Lock _gate = new();

    // A set rather than "the last kind said": the two can alternate within one process, and a
    // single field would then print the sentence on every call — the wallpaper this exists to
    // avoid, arrived at by the code meant to prevent it.
    private readonly HashSet<ResidencyShortfall> _said = [];

    /// <summary>Shared by everything rendering a report line in this process.</summary>
    public static ResidencyAdvice Shared { get; } = new();

    /// <summary>
    /// The advice, or null when this answer needs none — either it is healthy, or the same kind
    /// of shortfall has already been explained in this process.
    /// </summary>
    public string? AdviceFor(ResidencyShortfall shortfall)
    {
        if (shortfall == ResidencyShortfall.None)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_said.Add(shortfall))
            {
                return null;
            }
        }

        // One sentence for both kinds: which shortfall it is has already been named beside the
        // model, and the way back is the same. "after a restart" is not decoration — the broker
        // holds the policy it started with, so without it the command looks like it did nothing.
        return LocalLmText.ResidencyAdvice;
    }
}
