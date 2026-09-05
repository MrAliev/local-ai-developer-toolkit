namespace LocalAi.Contracts;

/// <summary>
/// Told what a long run is doing while it is still doing it.
///
/// The steps carry facts and no words: the console owns the sentences, because it is the only
/// face that prints them. An MCP server passes no observer and stays silent — progress on a
/// stdio server's standard error lands in the host's log for no reader's benefit.
///
/// Deliberately not <c>IProgress&lt;T&gt;</c>. With no synchronization context that posts to the
/// thread pool, and two lines can then arrive in the wrong order — which in a transcript is
/// indistinguishable from the run doing something else.
/// </summary>
public interface ILocalRunObserver
{
    void Report(LocalRunStep step);
}

/// <summary>One thing that happened during a run, named rather than worded.</summary>
public abstract record LocalRunStep;

/// <summary>
/// The job is in the broker and has not finished. Reported on every poll of the queue — ten
/// times a second — so the console can tell waiting apart from working without a clock of its
/// own down here. Whether any of them becomes a line is the console's decision.
/// </summary>
public sealed record BrokerJobPending(bool Running) : LocalRunStep;

/// <summary>
/// How far a download has got, as the broker wrote it down.
///
/// Two figures and no percent: <paramref name="Total"/> is the sum of the layer sizes the
/// backend has named so far, so it grows as layers appear, and a percent against a growing
/// denominator goes backwards. <paramref name="Phase"/> is one of ours - preparing, downloading,
/// verifying, storing, other - and <paramref name="Detail"/> carries the backend's own word only
/// when the phase is other.
/// </summary>
public sealed record ModelDownloadProgress(
    string Phase,
    string? Detail,
    long Completed,
    long Total) : LocalRunStep;

/// <summary>
/// About to translate one fragment of <paramref name="Total"/>. Reported before the call rather
/// than after it: the total is known before the first one, and it is the fact that decides
/// whether the reader waits.
/// </summary>
public sealed record TranslatingFragment(int Index, int Total) : LocalRunStep;

/// <summary>
/// The structure check failed, so the whole document is translated again from fragment 1 with
/// another model. Without this the counter restarting at 1 reads as a defect.
/// </summary>
public sealed record TranslationRetrying(string Detail, string Model) : LocalRunStep;

/// <summary>
/// Before the capacity probe, which loads a model to find out whether it fits. Minutes of
/// silence otherwise, before anything else about the run can be said.
/// </summary>
public sealed record TriageChoosingModel : LocalRunStep;

/// <summary>
/// About to analyse one fragment. No total: fragments are streamed off the log, and the count is
/// not known until the log ends.
/// </summary>
public sealed record TriagingFragment(int Index) : LocalRunStep;

/// <summary>The total, at the first moment it is true.</summary>
public sealed record TriageLogRead(int Fragments) : LocalRunStep;

/// <summary>
/// Merging partial findings. Also happens mid-stream when a level overflows its budget, so this
/// must not be read as "the log has been read".
/// </summary>
public sealed record TriageMerging(int Findings, int Level) : LocalRunStep;
