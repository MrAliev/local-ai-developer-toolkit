using LocalAi.Contracts;

namespace LocalAi.Launcher;

public sealed record VersionStopResult(
    string? Version,
    bool StoppedAnything,
    bool BrokerDrained);

/// <summary>
/// Stops the tools running out of one published version, without touching the pointer.
///
/// Activation already stops them as part of switching, but by then it is too late for the
/// step before it: replacing the stable launcher binary. Windows refuses to overwrite a
/// running executable, and every connected client keeps one launcher process alive per tool,
/// so an installation could publish the new version, fail to put the new launcher in place,
/// and roll the whole thing back — the recovery for it sitting one step further on.
///
/// Exposing the stop on its own makes that order expressible: stop, replace, activate.
///
/// The broker is asked to finish first and killed only if it will not. It owns a durable
/// queue and can be minutes into an inference, so terminating it is the answer of last resort
/// rather than the mechanism. The stdio tools have neither durable state nor a channel to ask
/// through, and their client restarts them on demand, so for those termination is all there
/// is — and all that is needed.
/// </summary>
public sealed class VersionStopper
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly VersionResolver resolver;
    private readonly LocalAiProcessController processController;
    private readonly string runtimeRoot;
    private readonly TimeSpan stopTimeout;
    private readonly TimeSpan drainTimeout;
    private readonly Action<TimeSpan> wait;

    public VersionStopper(
        string binRoot,
        LocalAiProcessController processController,
        TimeSpan stopTimeout,
        TimeSpan? drainTimeout = null,
        Action<TimeSpan>? wait = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        if (stopTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        }

        var canonicalBinRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binRoot));
        runtimeRoot = Path.GetDirectoryName(canonicalBinRoot)
            ?? throw new ArgumentException(
                "The LocalAi bin directory has no runtime root.",
                nameof(binRoot));
        resolver = new VersionResolver(canonicalBinRoot);
        this.processController = processController
            ?? throw new ArgumentNullException(nameof(processController));
        this.stopTimeout = stopTimeout;
        this.drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30);
        this.wait = wait ?? Thread.Sleep;
    }

    /// <summary>
    /// Stops the processes owned by <paramref name="version"/>, or by the active version when
    /// none is named. A machine with no pointer yet has nothing to stop and says so rather
    /// than failing: a first installation must not have to explain an error.
    /// </summary>
    public VersionStopResult Stop(string? version)
    {
        string resolved;
        if (version is null)
        {
            try
            {
                resolved = resolver.ReadCurrent().Version;
            }
            catch (LauncherException exception) when (
                exception.Code is "current_pointer_missing")
            {
                return new(null, false, false);
            }
        }
        else
        {
            resolved = version;
        }

        var versionDirectory = resolver.ValidateVersion(resolved);
        var owned = processController.SelectOwnedByVersion(
            versionDirectory,
            processController.Snapshot());
        if (owned.Count == 0)
        {
            return new(resolved, false, false);
        }

        var drained = false;
        if (owned.FirstOrDefault(process => process.BrokerAssemblyPath is not null) is { } broker)
        {
            drained = DrainBroker(broker);
        }

        // Whatever is left: the stdio tools, and a broker that would not go quietly.
        processController.StopOwnedByVersion(versionDirectory, stopTimeout);
        return new(resolved, true, drained);
    }

    private bool DrainBroker(ProcessSnapshot broker)
    {
        BrokerShutdownRequestStore.Write(
            runtimeRoot,
            new BrokerShutdownRequest(broker.ProcessId, broker.StartedAtUtc));
        try
        {
            for (var waited = TimeSpan.Zero; waited < drainTimeout; waited += PollInterval)
            {
                if (!IsRunning(broker))
                {
                    return true;
                }

                wait(PollInterval);
            }

            return !IsRunning(broker);
        }
        finally
        {
            // A broker that never read the request must not find it later and shut down in the
            // middle of somebody else's work.
            BrokerShutdownRequestStore.Delete(runtimeRoot);
        }
    }

    private bool IsRunning(ProcessSnapshot broker) =>
        processController.Snapshot().Any(process =>
            process.ProcessId == broker.ProcessId &&
            process.StartedAtUtc == broker.StartedAtUtc);
}
