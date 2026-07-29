using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAi.Launcher;

public sealed class VersionActivator
{
    private static readonly TimeSpan ActivationMutexTimeout =
        TimeSpan.FromSeconds(15);
    private readonly string _binRoot;
    private readonly string _currentPath;
    private readonly VersionResolver _resolver;
    private readonly LocalAiProcessController _processController;
    private readonly TimeSpan _leaseTimeout;
    private readonly TimeSpan _stopTimeout;
    private readonly string _mutexName;

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
        _mutexName = CreateMutexName(_binRoot);
    }

    public void Activate(string version, bool stopRunning)
    {
        var temporaryPath = Path.Combine(
            _binRoot,
            $"current.{Guid.NewGuid():N}.tmp");
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(ActivationMutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                throw new LauncherException(
                    "activation_timeout",
                    "Timed out waiting for another LocalAi activation.");
            }

            _resolver.ValidateVersion(version);
            var currentDirectory = File.Exists(_currentPath)
                ? _resolver.Resolve("localai").VersionDirectory
                : null;
            if (stopRunning && currentDirectory is not null)
            {
                _processController.StopOwnedByVersion(
                    currentDirectory,
                    _stopTimeout);
            }

            using var lease = VersionLease.AcquireExclusive(
                Path.Combine(_binRoot, "current.lock"),
                _leaseTimeout);
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

            _resolver.ValidateVersion(version);
            WritePointer(temporaryPath, version);
            File.Move(temporaryPath, _currentPath, overwrite: true);
            var committed = _resolver.ReadCurrent();
            if (!string.Equals(
                    committed.Version,
                    version,
                    StringComparison.Ordinal))
            {
                throw new LauncherException(
                    "current_pointer_invalid",
                    "Committed LocalAi current-version pointer did not read back.");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static void WritePointer(string temporaryPath, string version)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new VersionPointer(1, version));
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

    private static string CreateMutexName(string binRoot)
    {
        var normalized = OperatingSystem.IsWindows()
            ? binRoot.ToUpperInvariant()
            : binRoot;
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return (OperatingSystem.IsWindows() ? @"Global\" : string.Empty) +
               "LocalAi.Launcher.Activation." + hash;
    }
}
