using LocalAi.Contracts.Activation;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace LocalAi.Launcher;

public sealed class VersionActivator
{
    private readonly string _binRoot;
    private readonly string _currentPath;
    private readonly VersionResolver _resolver;
    private readonly LocalAiProcessController _processController;
    private readonly TimeSpan _leaseTimeout;
    private readonly TimeSpan _stopTimeout;

    public VersionActivator(
        string binRoot,
        LocalAiProcessController processController,
        TimeSpan leaseTimeout,
        TimeSpan stopTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        if (leaseTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseTimeout));
        }

        if (stopTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        }

        _binRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binRoot));
        _currentPath = Path.Combine(_binRoot, "current.json");
        _resolver = new VersionResolver(_binRoot);
        _processController = processController
            ?? throw new ArgumentNullException(nameof(processController));
        _leaseTimeout = leaseTimeout;
        _stopTimeout = stopTimeout;
    }

    public void Activate(
        string version,
        bool stopRunning,
        CurrentPointerExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        var temporaryPath = Path.Combine(
            _binRoot,
            $"current.{Guid.NewGuid():N}.tmp");
        try
        {
            using var startupGate = ActivationCoordinator.AcquireStartupGate(
                _binRoot,
                _leaseTimeout);
            CurrentPointerSnapshot observed;
            try
            {
                observed = CurrentPointerSnapshot.Read(startupGate);
                expectation.Validate(observed);
            }
            catch (CurrentPointerChangedException exception)
            {
                throw new LauncherException(
                    "current_pointer_changed",
                    exception.Message);
            }

            _resolver.ValidateVersion(version);
            var currentDirectory = observed.Exists
                ? _resolver.ValidateVersion(observed.Version!)
                : null;
            if (currentDirectory is not null)
            {
                if (stopRunning)
                {
                    _processController.StopOwnedByVersion(
                        currentDirectory,
                        _stopTimeout);
                }
                else if (_processController.HasOwnedByVersion(currentDirectory))
                {
                    throw new LauncherException(
                        "version_in_use",
                        "The active LocalAi version is currently in use.");
                }
            }

            using var lease = ActivationCoordinator.AcquireExclusive(
                startupGate,
                _leaseTimeout);
            try
            {
                expectation.Validate(CurrentPointerSnapshot.Read(lease));
            }
            catch (CurrentPointerChangedException exception)
            {
                throw new LauncherException(
                    "current_pointer_changed",
                    exception.Message);
            }

            _resolver.ValidateVersion(version);
            WritePointer(temporaryPath, version);
            File.Move(temporaryPath, _currentPath, overwrite: true);
            var committed = CurrentPointerSnapshot.Read(lease);
            var expectedBytes = CurrentPointerSnapshot.CreateCanonicalBytes(version);
            if (!committed.Exists || !committed.IsCanonical ||
                !string.Equals(committed.Version, version, StringComparison.Ordinal) ||
                !committed.RawBytes.SequenceEqual(expectedBytes))
            {
                throw new LauncherException(
                    "current_pointer_invalid",
                    "Committed LocalAi current-version pointer did not read back.");
            }
        }
        catch (ActivationCoordinationException exception)
        {
            throw new LauncherException(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is CurrentPointerException or JsonException or DecoderFallbackException)
        {
            throw new LauncherException(
                "current_pointer_invalid",
                "The LocalAi current-version pointer is invalid.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new LauncherException(
                "activation_failed",
                "LocalAi activation failed.");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or Win32Exception)
            {
            }
        }
    }

    private static void WritePointer(string temporaryPath, string version)
    {
        var bytes = CurrentPointerSnapshot.CreateCanonicalBytes(version);
        using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

}
