using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// Runs the update check on the one long-lived process there is, and keeps it out of the way
/// of everything that process exists to do.
///
/// The rule this is built around: no queued job ever waits on GitHub. The check runs on its
/// own loop, never on the job path, and a failure of any kind is recorded rather than raised —
/// a broker that could be stopped by a release page is a worse broker than one that never
/// looked.
///
/// The consent is read on every pass, not captured at start, so switching the check off takes
/// effect at the next pass instead of at the next restart. Switching it on is the one that
/// still waits for a restart of nothing: the loop is already running and simply starts finding
/// itself permitted.
/// </summary>
public sealed class UpdateCheckService(
    string runtimeRoot,
    Func<DateTimeOffset, CancellationToken, Task<UpdateCheckState>> check,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// How often the loop wakes to ask whether a check is due. Not the interval between
    /// checks, which is the policy's: this is only the resolution at which the policy is
    /// noticed, and it is coarse because nothing here is urgent.
    /// </summary>
    public static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(15);

    private readonly string runtimeRoot = string.IsNullOrWhiteSpace(runtimeRoot)
        ? throw new ArgumentException("A runtime root is required.", nameof(runtimeRoot))
        : Path.GetFullPath(runtimeRoot);

    private readonly Func<DateTimeOffset, CancellationToken, Task<UpdateCheckState>> check =
        check ?? throw new ArgumentNullException(nameof(check));

    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// One pass: read the consent, decide whether a check is due, and record what it found.
    /// Returns whether a check actually ran, which is what the tests assert on and what makes
    /// "did this talk to the network" a question with an answer.
    /// </summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var policy = new UpdateCheckPolicyStore(runtimeRoot).Read();
        var stateStore = new UpdateCheckStateStore(runtimeRoot);
        var state = stateStore.Read();
        var now = timeProvider.GetUtcNow();
        // The seed is the runtime root: stable for this installation, different between
        // machines, and nothing anybody would call an identifier — it never leaves the disk.
        if (!UpdateCheckSchedule.IsDue(policy, state, now, runtimeRoot))
        {
            return false;
        }

        UpdateCheckState result;
        try
        {
            result = await check(now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The probe already turns its own failures into "unavailable"; this is the guard
            // for anything it did not anticipate. The check is the least important thing this
            // process does, and it must never be the thing that stops it.
            result = new UpdateCheckState(1, UpdateCheckStatus.Unavailable, now, null, null);
        }

        try
        {
            stateStore.Write(result);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // An unwritable state file costs this pass its record, and the next pass will find
            // the check due again — which is the right outcome for a disk that is full or a
            // directory somebody is holding.
            return true;
        }

        return true;
    }

    /// <summary>
    /// The loop, for the broker to leave running in the background. It swallows everything
    /// except cancellation, because there is no failure here worth ending a broker over.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
            }

            try
            {
                await Task.Delay(PassInterval, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
