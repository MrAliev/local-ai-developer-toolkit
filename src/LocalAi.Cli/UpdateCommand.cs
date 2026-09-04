using LocalAi.Broker;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Cli;

/// <summary>
/// Installs the newest release over this one, in one command.
///
/// It is the applying half of update awareness, and it is deliberately narrow: the binaries,
/// and nothing else. Prerequisites, models and client integrations are the wizard's business —
/// an update is the case where all of those are already set up and only the version is behind.
///
/// Every guarantee an installation makes is kept, because this reuses the machinery that makes
/// them: the manifest is verified against the embedded key before the package is fetched, the
/// package against the manifest before anything is extracted, the version directory is
/// immutable, and the pointer swap is atomic and reversible. The only thing this adds is the
/// decision to do it — which is why it never happens by itself, only when somebody types it.
/// </summary>
public static class UpdateCommand
{
    /// <summary>
    /// How long to keep waiting for the queue to drain when asked to. Long enough for an
    /// inference to finish, short enough that a forgotten `--wait` does not sit in a terminal
    /// overnight.
    /// </summary>
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static Task<int> ExecuteAsync(
        string[] args,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            args,
            ModelResidencyPolicyStore.DefaultRuntimeRoot,
            Console.Out,
            Console.Error,
            feed: null,
            processRunner: null,
            cancellationToken);

    /// <summary>
    /// The runtime root, the writers and the feed are named so a test can run this against a
    /// directory and a signed local release of its own, rather than against the machine and
    /// GitHub.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        string[] args,
        string runtimeRoot,
        TextWriter output,
        TextWriter error,
        IReleaseFeed? feed = null,
        IProcessRunner? processRunner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Any(argument => argument is "--help" or "-h" or "/?"))
        {
            return Usage(error);
        }

        var wait = args.Contains("--wait", StringComparer.Ordinal);
        var force = args.Contains("--force", StringComparer.Ordinal);
        var unknown = args.FirstOrDefault(argument =>
            argument is not ("--wait" or "--force"));
        if (unknown is not null)
        {
            error.WriteLine(CliText.UpdateUnknownOption(unknown));
            return Usage(error);
        }

        var installed = InstalledVersionReader.Read(runtimeRoot);
        if (!installed.Exists)
        {
            error.WriteLine(
                CliText.UpdateNoInstallation(runtimeRoot));
            return 1;
        }

        using var owned = feed is null ? new AnonymousReleaseFeed() : null;
        var releases = feed ?? owned!;
        string tag;
        ResolvedRelease resolved;
        var working = Path.Combine(
            Path.GetTempPath(),
            "localai-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            output.WriteLine(CliText.UpdateLookingForRelease);
            tag = await releases.ResolveTagAsync("latest", cancellationToken)
                .ConfigureAwait(false);
            Directory.CreateDirectory(working);
            resolved = await releases.ResolveAsync(tag, working, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ReleaseResolutionException exception)
        {
            // Includes a manifest that did not verify: what cannot be proven is not installed.
            error.WriteLine(exception.Message);
            Delete(working);
            return 1;
        }

        var available = resolved.Manifest.ReleaseVersion;
        if (!force && IsCurrent(resolved.Manifest, installed))
        {
            output.WriteLine(CliText.UpdateNothingToInstall(available));
            Delete(working);
            return 0;
        }

        // The doctor report's sentence, reused rather than paraphrased. Its third hole is a
        // release page URL, which this command does not have: the feed resolves a package URI.
        output.WriteLine(
            CliText.UpdateAvailable(available, installed.DisplayName, null).TrimEnd());
        try
        {
            // Inside the block that owns the working directory, not before it. Waiting on the
            // queue is the one part of this command that can take minutes, so it is the part
            // somebody interrupts - and cancelling used to throw past the only code that deletes
            // the directory, leaving a working directory in the temp root on every abandoned
            // wait with nothing to collect them (#319).
            if (!await QueueIsQuietAsync(runtimeRoot, wait, output, error, cancellationToken)
                .ConfigureAwait(false))
            {
                return 2;
            }

            var service = new ReleaseInstallService(
                releases,
                processRunner ?? new SystemProcessRunner(),
                new SystemFileSystemProbe());
            var lastReported = -1L;
            var result = await service.InstallAsync(
                    resolved,
                    working,
                    tag,
                    new Progress<long>(bytes => ReportDownload(
                        output,
                        bytes,
                        resolved.Manifest.PackageSize,
                        ref lastReported)),
                    // The binaries and nothing else: an update is the case where the models,
                    // the prerequisites and the client registrations are already in place.
                    ModelProvisioningSelection.None,
                    gpu: null,
                    modelProgress: null,
                    activated: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Installed)
            {
                error.WriteLine(
                    CliText.UpdateDidNotComplete(result.Status, result.Reason).TrimEnd());
                return 1;
            }

            output.WriteLine(CliText.UpdateInstalled(available, result.VersionPath));
            output.WriteLine(CliText.UpdateClientsPickItUp);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine(CliText.UpdateCancelled);
            return 1;
        }
        catch (Exception exception) when (
            exception is LocalAiPackageInstallationException or ReleaseResolutionException or
                ReleaseVerificationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine(CliText.UpdateFailed(exception.Message));
            return 1;
        }
        finally
        {
            Delete(working);
        }
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine(CliText.UpdateUsage((int)MaximumWait.TotalMinutes));
        return 2;
    }

    /// <summary>
    /// Whether the broker has nothing queued, waiting for it if asked.
    ///
    /// Activation stops the tools running out of the current version, so updating underneath
    /// queued work would abandon somebody's inference halfway. Refusing is the default because
    /// a person who typed one command did not ask to lose a job; `--wait` is for the person who
    /// meant it and is willing to sit through the queue.
    /// </summary>
    private static async Task<bool> QueueIsQuietAsync(
        string runtimeRoot,
        bool wait,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var queue = new DurableQueue(runtimeRoot);
        var deadline = DateTimeOffset.UtcNow + MaximumWait;
        var announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int queued;
            try
            {
                queued = (await queue.ListQueuedAsync(cancellationToken).ConfigureAwait(false))
                    .Count;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A queue that cannot be read is not a queue holding work: the runtime may
                // never have run. Refusing here would block an update on a directory that does
                // not exist yet.
                return true;
            }

            if (queued == 0)
            {
                return true;
            }

            if (!wait)
            {
                error.WriteLine(CliText.UpdateQueueBusy(queued));
                return false;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                error.WriteLine(CliText.UpdateQueueStillBusy(
                    queued,
                    (int)MaximumWait.TotalMinutes));
                return false;
            }

            if (!announced)
            {
                output.WriteLine(CliText.UpdateQueueWaiting(queued));
                announced = true;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Progress in whole percent, and only when it moves. A download line that rewrites itself
    /// hundreds of times is noise in a terminal and a wall of text in a log.
    /// </summary>
    private static void ReportDownload(
        TextWriter output,
        long bytes,
        long total,
        ref long lastReported)
    {
        if (total <= 0)
        {
            return;
        }

        var percent = Math.Clamp(100 * bytes / total, 0, 100);
        if (percent == lastReported || percent % 10 != 0)
        {
            return;
        }

        lastReported = percent;
        output.WriteLine($"  downloading: {percent}%");
    }

    /// <summary>
    /// Whether the resolved release is the one already installed.
    ///
    /// Asked through the same comparison every other surface uses, so this command cannot
    /// drift back into comparing a release version against a commit id and concluding, as it
    /// did, that there was nothing to do (#255). The manifest carries both halves, so the
    /// fallback — same version directory — answers even on an installation that never
    /// recorded which release it came from.
    /// </summary>
    private static bool IsCurrent(ReleaseManifest manifest, InstalledVersion installed) =>
        UpdateComparison.Compare(
            new UpdateCheckState(
                1,
                UpdateCheckStatus.Verified,
                null,
                manifest.ReleaseVersion,
                null,
                manifest.VersionDirectory),
            installed) == UpdateAvailability.UpToDate;

    private static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
