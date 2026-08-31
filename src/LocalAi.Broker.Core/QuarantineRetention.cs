using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// One quarantined job as the sweep sees it. <paramref name="QuarantinedAtUtc"/> comes from
/// the marker the queue writes at the moment of the move: the directory's own timestamps
/// travel with the rename and describe the job's activity, not its quarantining, and the
/// grace below protects the investigation window, which starts at the move.
/// </summary>
public sealed record QuarantinedJobSnapshot(
    string Directory,
    DateTimeOffset QuarantinedAtUtc,
    long Bytes);

/// <summary>
/// Decides which quarantined jobs to forget (#204).
///
/// Quarantine exists so one corrupt record does not stop the pipeline and stays available
/// for inspection — but its entries hold full request bodies, prompts and images included,
/// and no bound ever covered them: a rare corrupt job became a permanent privacy and disk
/// record. Three bounds now apply — age, entry count, byte budget — and nothing younger
/// than the quarantine grace is ever touched by any of them: a fresh entry exists precisely
/// to be looked at.
/// </summary>
public static class QuarantineRetention
{
    public static IReadOnlyList<string> Plan(
        IReadOnlyList<QuarantinedJobSnapshot> entries,
        RuntimeRetentionPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(policy);
        var graceCutoff = now - policy.QuarantineGrace;
        var expendable = entries
            .Where(entry => entry.QuarantinedAtUtc <= graceCutoff)
            .OrderBy(entry => entry.QuarantinedAtUtc)
            .ToList();
        var doomed = new List<string>();
        var doomedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Doom(QuarantinedJobSnapshot entry)
        {
            if (doomedSet.Add(entry.Directory))
            {
                doomed.Add(entry.Directory);
            }
        }

        var ageCutoff = now - policy.QuarantineRetention;
        foreach (var entry in expendable.Where(entry => entry.QuarantinedAtUtc <= ageCutoff))
        {
            Doom(entry);
        }

        // The count and byte bounds measure the whole quarantine, but only entries past the
        // grace may pay for them: a burst of fresh corruption never deletes the very entries
        // somebody is about to inspect.
        var surviving = entries.Count - doomed.Count;
        foreach (var entry in expendable)
        {
            if (surviving <= policy.QuarantineEntryLimit)
            {
                break;
            }

            if (doomedSet.Contains(entry.Directory))
            {
                continue;
            }

            Doom(entry);
            surviving--;
        }

        var retainedBytes = entries
            .Where(entry => !doomedSet.Contains(entry.Directory))
            .Sum(entry => entry.Bytes);
        foreach (var entry in expendable)
        {
            if (retainedBytes <= policy.QuarantineBudgetBytes)
            {
                break;
            }

            if (doomedSet.Contains(entry.Directory))
            {
                continue;
            }

            Doom(entry);
            retainedBytes -= entry.Bytes;
        }

        return doomed;
    }
}
