using System.ComponentModel;
using System.Diagnostics;
using LocalAi.Contracts;

namespace LocalAi.Launcher;

public sealed record ProcessSnapshot(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath,
    string? BrokerAssemblyPath);

public sealed class LocalAiProcessController
{
    private readonly Func<IReadOnlyList<ProcessSnapshot>> _snapshot;
    private readonly Action<ProcessSnapshot, TimeSpan> _stop;
    private readonly StringComparison _pathComparison;

    public LocalAiProcessController(string? runtimeRoot = null)
    {
        var root = Path.GetFullPath(
            runtimeRoot ?? ModelResidencyPolicyStore.DefaultRuntimeRoot);
        _snapshot = () => CaptureSnapshots(root);
        _stop = StopProcess;
        _pathComparison = PathComparison;
    }

    public LocalAiProcessController(
        Func<IReadOnlyList<ProcessSnapshot>> snapshot,
        Action<ProcessSnapshot, TimeSpan> stop)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        _pathComparison = PathComparison;
    }

    /// <summary>
    /// The processes as they are right now. Exposed so a caller can ask a broker to finish its
    /// work and then watch for it to go, rather than only being able to kill it.
    /// </summary>
    public IReadOnlyList<ProcessSnapshot> Snapshot() => _snapshot();

    public IReadOnlyList<ProcessSnapshot> SelectOwnedByVersion(
        string versionDirectory,
        IReadOnlyList<ProcessSnapshot> snapshots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDirectory);
        ArgumentNullException.ThrowIfNull(snapshots);
        var canonicalVersion = CanonicalizePath(versionDirectory);
        return snapshots
            .Where(snapshot =>
                IsBelow(snapshot.ExecutablePath, canonicalVersion) ||
                snapshot.BrokerAssemblyPath is { } brokerAssembly &&
                IsBelow(brokerAssembly, canonicalVersion))
            .ToArray();
    }

    public void StopOwnedByVersion(string versionDirectory, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        foreach (var process in SelectOwnedByVersion(versionDirectory, _snapshot()))
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            _stop(process, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    public bool HasOwnedByVersion(string versionDirectory) =>
        SelectOwnedByVersion(versionDirectory, _snapshot()).Count > 0;

    private bool IsBelow(string path, string root)
    {
        try
        {
            var prefix = Path.TrimEndingDirectorySeparator(root) +
                         Path.DirectorySeparatorChar;
            return CanonicalizePath(path).StartsWith(prefix, _pathComparison);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ProcessSnapshot> CaptureSnapshots(string runtimeRoot)
    {
        var broker = new BrokerHostStateReader().ReadFreshOwnership(runtimeRoot);
        var snapshots = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var startedAt = new DateTimeOffset(
                        process.StartTime.ToUniversalTime(),
                        TimeSpan.Zero);
                    var brokerAssemblyPath =
                        broker is not null &&
                        broker.ProcessId == process.Id &&
                        broker.StartedAtUtc == startedAt
                            ? broker.BrokerAssemblyPath
                            : null;
                    string executablePath;
                    try
                    {
                        executablePath = process.MainModule?.FileName
                            ?? string.Empty;
                    }
                    catch (Exception exception) when (
                        exception is Win32Exception or
                        InvalidOperationException or
                        NotSupportedException)
                    {
                        if (brokerAssemblyPath is null)
                        {
                            continue;
                        }

                        executablePath = string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(executablePath) &&
                        brokerAssemblyPath is null)
                    {
                        continue;
                    }

                    snapshots.Add(new ProcessSnapshot(
                        process.Id,
                        startedAt,
                        executablePath,
                        brokerAssemblyPath));
                }
                catch (Exception exception) when (
                    exception is Win32Exception or
                    InvalidOperationException or
                    NotSupportedException)
                {
                }
            }
        }

        return snapshots;
    }

    private static void StopProcess(ProcessSnapshot snapshot, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            var startedAt = new DateTimeOffset(
                process.StartTime.ToUniversalTime(),
                TimeSpan.Zero);
            if (startedAt != snapshot.StartedAtUtc || process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            var milliseconds = timeout >= TimeSpan.FromMilliseconds(int.MaxValue)
                ? int.MaxValue
                : Math.Max(0, (int)timeout.TotalMilliseconds);
            if (!process.WaitForExit(milliseconds))
            {
                throw new LauncherException(
                    snapshot.BrokerAssemblyPath is null
                        ? "activation_timeout"
                        : "broker_still_running",
                    $"LocalAi process {snapshot.ProcessId} did not exit.");
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception exception)
        {
            throw new LauncherException(
                snapshot.BrokerAssemblyPath is null
                    ? "activation_timeout"
                    : "broker_still_running",
                $"Could not stop LocalAi process {snapshot.ProcessId}: {exception.Message}");
        }
    }

    private static string CanonicalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        FileSystemInfo? info = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : File.Exists(fullPath)
                ? new FileInfo(fullPath)
                : null;
        return info is not null &&
               (info.Attributes & FileAttributes.ReparsePoint) != 0
            ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath
            : fullPath;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

}
