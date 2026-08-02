namespace LocalAi.Launcher;

public sealed record VersionStopResult(string? Version, bool StoppedAnything);

/// <summary>
/// Stops the tools running out of one published version, without touching the pointer.
///
/// Activation already stops them as part of switching, but by then it is too late for the
/// step before it: replacing the stable launcher binary. Windows refuses to overwrite a
/// running executable, and every connected client keeps one launcher process alive per tool,
/// so an installation could publish the new version, fail to put the new launcher in place,
/// and roll the whole thing back — the recovery for it sitting one step further on.
///
/// Exposing the stop on its own makes that order expressible: stop, replace, activate. It is
/// also the answer to "why can I not install while my editor is open", which previously had
/// no answer that did not involve closing the editor.
/// </summary>
public sealed class VersionStopper
{
    private readonly VersionResolver resolver;
    private readonly LocalAiProcessController processController;
    private readonly TimeSpan stopTimeout;

    public VersionStopper(
        string binRoot,
        LocalAiProcessController processController,
        TimeSpan stopTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        if (stopTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        }

        resolver = new VersionResolver(binRoot);
        this.processController = processController
            ?? throw new ArgumentNullException(nameof(processController));
        this.stopTimeout = stopTimeout;
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
                return new(null, false);
            }
        }
        else
        {
            resolved = version;
        }

        var versionDirectory = resolver.ValidateVersion(resolved);
        if (!processController.HasOwnedByVersion(versionDirectory))
        {
            return new(resolved, false);
        }

        processController.StopOwnedByVersion(versionDirectory, stopTimeout);
        return new(resolved, true);
    }
}
