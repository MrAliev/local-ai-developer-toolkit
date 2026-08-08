using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// One archived job as the sweep sees it, before anything is opened or parsed.
///
/// <paramref name="TerminalAtUtc"/> comes from the last write time of <c>state.json</c>, which is
/// the moment the job reached its terminal state: the state document is written and only then is
/// the directory moved into the archive, and neither the move nor anything afterwards touches the
/// file. Stat is used rather than a parse because a machine can hold thousands of archived jobs
/// and this runs under the queue mutex; the entries the plan actually names are re-read and
/// confirmed against their own recorded timestamp before anything is deleted.
/// </summary>
public sealed record ArchivedJobSnapshot(
    string Directory,
    DateTimeOffset TerminalAtUtc,
    long? ResponseBytes);

/// <summary>
/// What a sweep intends to do, in the order it intends to do it.
/// </summary>
public sealed record ArchiveRetentionPlan(
    IReadOnlyList<string> DirectoriesToDelete,
    IReadOnlyList<string> ResponsesToDrop)
{
    public static ArchiveRetentionPlan Empty { get; } = new([], []);

    public int ActionCount => DirectoriesToDelete.Count + ResponsesToDrop.Count;
}

/// <summary>
/// Decides which archived jobs to forget.
///
/// The archive exists so a client can still collect a response after the job directory has left
/// <c>jobs/</c>. That is a handover, not a record: the client reads it on its next poll and never
/// asks again, yet an embedding batch leaves eleven megabytes of vectors behind and nothing ever
/// removed them. Thousands of indexed commits later that is twenty gigabytes of data no reader
/// exists for.
///
/// Three bounds apply, in this order:
///
/// <list type="bullet">
/// <item>whole entries older than the archive retention, and entries beyond the entry limit,
/// are deleted outright;</item>
/// <item>response bodies older than the response retention are dropped, leaving the request and
/// state documents — a few hundred bytes — as the audit trail;</item>
/// <item>if the retained bodies still exceed the byte budget, the oldest are dropped until they
/// fit.</item>
/// </list>
///
/// Nothing younger than the response grace is ever touched, by any of the three. That is the one
/// rule here that protects a running client rather than the disk.
/// </summary>
public static class ArchiveRetention
{
    public static ArchiveRetentionPlan Plan(
        IReadOnlyList<ArchivedJobSnapshot> entries,
        RuntimeRetentionPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(policy);
        policy = policy.Normalized();
        if (entries.Count == 0)
        {
            return ArchiveRetentionPlan.Empty;
        }

        // Oldest first: every bound below removes from the old end, and the budget pass needs the
        // newest entries to be the ones it stops at.
        var ordered = entries
            .OrderBy(entry => entry.TerminalAtUtc)
            .ThenBy(entry => entry.Directory, StringComparer.Ordinal)
            .ToArray();
        var expendable = ordered
            .Where(entry => now - entry.TerminalAtUtc >= policy.ResponseGrace)
            .ToArray();

        var deletions = new List<string>();
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var overLimit = Math.Max(0, ordered.Length - policy.ArchiveEntryLimit);
        foreach (var entry in expendable)
        {
            if (deletions.Count >= policy.MaximumActionsPerSweep)
            {
                break;
            }

            var expired = now - entry.TerminalAtUtc >= policy.ArchiveRetention;
            if (!expired && deletions.Count >= overLimit)
            {
                break;
            }

            deletions.Add(entry.Directory);
            deleted.Add(entry.Directory);
        }

        var drops = new List<string>();
        var budget = policy.MaximumActionsPerSweep - deletions.Count;
        if (budget <= 0)
        {
            return new ArchiveRetentionPlan(deletions, drops);
        }

        var retainedBytes = ordered
            .Where(entry => !deleted.Contains(entry.Directory))
            .Sum(entry => entry.ResponseBytes ?? 0);
        foreach (var entry in expendable)
        {
            if (drops.Count >= budget)
            {
                break;
            }

            if (entry.ResponseBytes is not { } bytes || deleted.Contains(entry.Directory))
            {
                continue;
            }

            var aged = now - entry.TerminalAtUtc >= policy.ResponseRetention;
            if (!aged && retainedBytes <= policy.ResponseBudgetBytes)
            {
                break;
            }

            drops.Add(entry.Directory);
            retainedBytes -= bytes;
        }

        return new ArchiveRetentionPlan(deletions, drops);
    }
}
