using LocalAi.Broker;
using LocalAi.Contracts;
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
            error.WriteLine($"Unknown option '{unknown}'.");
            return Usage(error);
        }

        var installed = InstalledVersion(runtimeRoot);
        if (installed is null)
        {
            error.WriteLine(
                "There is no LocalAi installation at " + runtimeRoot + " to update. Run the " +
                "installer to set one up.");
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
            output.WriteLine("Looking for the newest release...");
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
        if (!IsNewer(available, installed) && !force)
        {
            output.WriteLine(
                $"LocalAi {installed} is already the newest release ({available}).");
            Delete(working);
            return 0;
        }

        output.WriteLine($"LocalAi {available} is available; this installation is {installed}.");
        if (!await QueueIsQuietAsync(runtimeRoot, wait, output, error, cancellationToken)
            .ConfigureAwait(false))
        {
            Delete(working);
            return 2;
        }

        try
        {
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
                error.WriteLine($"Update did not complete: {result.Status}. {result.Reason}".TrimEnd());
                return 1;
            }

            output.WriteLine(
                $"Installed LocalAi {result.Version} at {result.VersionPath}.");
            output.WriteLine(
                "Clients pick it up the next time they start a tool; nothing running was " +
                "restarted for you.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Update cancelled. The installed version was not changed.");
            return 1;
        }
        catch (Exception exception) when (
            exception is LocalAiPackageInstallationException or ReleaseResolutionException or
                ReleaseVerificationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine("Update failed: " + exception.Message);
            return 1;
        }
        finally
        {
            Delete(working);
        }
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine(
            """
            Usage: localai update [--wait] [--force]

              Installs the newest signed release over this one. The manifest is verified
              against the embedded release key before anything is downloaded, and the version
              pointer is swapped atomically once the package checks out.

              --wait   Wait for queued broker jobs to finish instead of refusing.
              --force  Install the newest release even when it is not newer than this one.

            Prerequisites, models and client integrations are not touched; run the installer
            for those.
            """);
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
                error.WriteLine(
                    $"The broker has {queued} queued job(s). Updating now would stop the tools " +
                    "running them. Run `localai update --wait` to update once they finish.");
                return false;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                error.WriteLine(
                    $"Still {queued} queued job(s) after {MaximumWait.TotalMinutes:N0} minutes. " +
                    "Nothing was changed; try again when the queue is quieter.");
                return false;
            }

            if (!announced)
            {
                output.WriteLine($"Waiting for {queued} queued job(s) to finish...");
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

    private static bool IsNewer(string available, string installed) =>
        new UpdateCheckState(1, UpdateCheckStatus.Verified, null, available, null)
            .IsNewerThan(installed);

    private static string? InstalledVersion(string runtimeRoot)
    {
        try
        {
            var pointerPath = Path.Combine(runtimeRoot, "bin", "current.json");
            if (!File.Exists(pointerPath))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(pointerPath));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or IOException or
                UnauthorizedAccessException)
        {
            return null;
        }
    }

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
