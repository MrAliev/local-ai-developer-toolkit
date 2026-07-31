using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Client;

public interface IBrokerProcess
{
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);
}

public sealed class BrokerProcess : IBrokerProcess
{
    private const int MaximumDiagnosticValueLength = 512;
    private static readonly TimeSpan MaximumSemaphoreWaitSlice =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StartupPollInterval =
        TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaximumHeartbeatSkew =
        TimeSpan.FromSeconds(5);

    private readonly string _executablePath;
    private readonly string _runtimeRoot;
    private readonly Func<string, BrokerProcessState?> _readState;
    private readonly Func<BrokerProcessState, bool> _isRunning;
    private readonly Func<string, string, IBrokerStartAttempt> _start;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _startupTimeout;
    private readonly string _arguments;
    private readonly string _startupSemaphoreName;

    public BrokerProcess(
        string executablePath,
        string runtimeRoot,
        string? ollamaUrl = null,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null)
        : this(
            executablePath,
            runtimeRoot,
            ReadState,
            IsRunning,
            StartAttempt,
            timeProvider ?? TimeProvider.System,
            Task.Delay,
            startupTimeout,
            BuildArguments(runtimeRoot, ollamaUrl))
    {
    }

    public static BrokerProcess CreateDefault(string runtimeRoot)
    {
        var brokerAssembly = Path.GetFullPath(typeof(DurableQueue).Assembly.Location);
        var arguments =
            Quote(brokerAssembly) + " " + BuildArguments(runtimeRoot, null);
        return new BrokerProcess(
            "dotnet",
            runtimeRoot,
            ReadState,
            IsRunning,
            StartAttempt,
            TimeProvider.System,
            Task.Delay,
            arguments: arguments);
    }

    public BrokerProcess(
        string executablePath,
        string runtimeRoot,
        Func<string, BrokerProcessState?> readState,
        Func<BrokerProcessState, bool> isRunning,
        Func<string, string, int> start,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan? startupTimeout = null,
        string? arguments = null,
        string? brokerAssemblyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _executablePath = executablePath;
        _runtimeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        _readState = readState ?? throw new ArgumentNullException(nameof(readState));
        _isRunning = isRunning ?? throw new ArgumentNullException(nameof(isRunning));
        ArgumentNullException.ThrowIfNull(start);
        _start = (executablePath, startArguments) =>
            new LegacyStartAttempt(start(executablePath, startArguments));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        if (_startupTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        _arguments = arguments ?? BuildArguments(_runtimeRoot, null);
        _startupSemaphoreName = CreateSemaphoreName(_runtimeRoot);
        _ = brokerAssemblyPath;
    }

    internal static BrokerProcess CreateForTesting(
        string executablePath,
        string runtimeRoot,
        Func<string, BrokerProcessState?> readState,
        Func<BrokerProcessState, bool> isRunning,
        Func<string, string, IBrokerStartAttempt> start,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan? startupTimeout = null,
        string? arguments = null,
        string? brokerAssemblyPath = null) =>
        new(
            executablePath,
            runtimeRoot,
            readState,
            isRunning,
            start,
            timeProvider,
            delay,
            startupTimeout,
            arguments,
            brokerAssemblyPath);

    private BrokerProcess(
        string executablePath,
        string runtimeRoot,
        Func<string, BrokerProcessState?> readState,
        Func<BrokerProcessState, bool> isRunning,
        Func<string, string, IBrokerStartAttempt> start,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan? startupTimeout = null,
        string? arguments = null,
        string? brokerAssemblyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _executablePath = executablePath;
        _runtimeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        _readState = readState ?? throw new ArgumentNullException(nameof(readState));
        _isRunning = isRunning ?? throw new ArgumentNullException(nameof(isRunning));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        if (_startupTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        _arguments = arguments ?? BuildArguments(_runtimeRoot, null);
        _startupSemaphoreName = CreateSemaphoreName(_runtimeRoot);
        _ = brokerAssemblyPath;
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        var startupTimestamp = _timeProvider.GetTimestamp();
        using var startupLock = await Task.Run(
            () => EnterSemaphore(startupTimestamp, cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var observation = ReadObservation();
        cancellationToken.ThrowIfCancellationRequested();
        if (observation.Status == BrokerObservationStatus.CompatibleHealthy)
        {
            return;
        }

        ThrowIfIncompatible(observation);
        if (observation.Status != BrokerObservationStatus.AbsentOrStale)
        {
            throw new InvalidOperationException(
                "Broker startup requires an absent or stale host state.");
        }

        using var startAttempt = _start(_executablePath, _arguments);
        cancellationToken.ThrowIfCancellationRequested();
        observation = ObserveStartAttempt(
            startAttempt,
            observation,
            cancellationToken);
        var lastObservation = observation.Detail;
        ThrowIfStartupTimedOut(startupTimestamp, lastObservation);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observation = ReadObservation();
            cancellationToken.ThrowIfCancellationRequested();
            lastObservation = observation.Detail;
            if (observation.Status == BrokerObservationStatus.CompatibleHealthy)
            {
                return;
            }

            ThrowIfIncompatible(observation);

            observation = ObserveStartAttempt(
                startAttempt,
                observation,
                cancellationToken);
            lastObservation = observation.Detail;

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfStartupTimedOut(startupTimestamp, lastObservation);
            var remaining = RemainingStartupBudget(startupTimestamp);
            await _delay(
                remaining < StartupPollInterval ? remaining : StartupPollInterval,
                cancellationToken);
        }
    }

    public static string StatePath(string runtimeRoot) =>
        Path.Combine(Path.GetFullPath(runtimeRoot), "host.json");

    private BrokerObservation Observe(BrokerProcessState? state)
    {
        if (state is null)
        {
            return new(
                BrokerObservationStatus.AbsentOrStale,
                "host state is absent or unreadable");
        }

        var heartbeatAge = _timeProvider.GetUtcNow() - state.HeartbeatAtUtc;
        if (heartbeatAge < -MaximumHeartbeatSkew ||
            heartbeatAge > MaximumHeartbeatSkew)
        {
            return new(
                BrokerObservationStatus.AbsentOrStale,
                "host heartbeat is stale");
        }

        try
        {
            if (!_isRunning(state))
            {
                return new(
                    BrokerObservationStatus.AbsentOrStale,
                    "host process is not the recorded owner");
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or
            ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException)
        {
            return new(
                BrokerObservationStatus.AbsentOrStale,
                "host process is not the recorded owner");
        }

        if (state.SchemaVersion != BrokerCompatibilityContract.HostStateSchemaVersion ||
            !BrokerCompatibilityContract.IsCurrent(state.Compatibility))
        {
            return new(
                BrokerObservationStatus.IncompatibleHealthy,
                CompatibilityDetail(state));
        }

        if (string.IsNullOrWhiteSpace(state.BrokerAssemblyPath))
        {
            return new(
                BrokerObservationStatus.IncompatibleHealthy,
                "host assembly path is missing");
        }

        return new(
            BrokerObservationStatus.CompatibleHealthy,
            "host broker assembly path: " + state.BrokerAssemblyPath);
    }

    private BrokerObservation ReadObservation()
    {
        try
        {
            return Observe(_readState(_runtimeRoot));
        }
        catch (Exception exception) when (
            exception is JsonException or
            IOException or
            UnauthorizedAccessException)
        {
            return new(
                BrokerObservationStatus.AbsentOrStale,
                "host state is absent or unreadable");
        }
    }

    private static string CompatibilityDetail(BrokerProcessState state) =>
        "expected schema=" + BrokerCompatibilityContract.HostStateSchemaVersion +
        " protocol=" + BrokerCompatibilityContract.ProtocolVersion +
        " build=" + BrokerCompatibilityContract.BuildCompatibilityId +
        "; actual schema=" + state.SchemaVersion.ToString(CultureInfo.InvariantCulture) +
        " protocol=" +
        (state.Compatibility?.ProtocolVersion.ToString(CultureInfo.InvariantCulture) ?? "missing") +
        " build=" + DiagnosticValue(state.Compatibility?.BuildCompatibilityId) +
        "; broker path=" + DiagnosticValue(state.BrokerAssemblyPath);

    private static string DiagnosticValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "missing";
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaximumDiagnosticValueLength));
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var normalizedRune = category is
                UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator
                ? new Rune('?')
                : rune;
            if (builder.Length + normalizedRune.Utf16SequenceLength >
                MaximumDiagnosticValueLength)
            {
                break;
            }

            builder.Append(normalizedRune.ToString());
        }

        return builder.ToString();
    }

    private static void ThrowIfIncompatible(BrokerObservation observation)
    {
        if (observation.Status == BrokerObservationStatus.IncompatibleHealthy)
        {
            throw new BrokerBootstrapException("broker_incompatible", observation.Detail);
        }
    }

    private static BrokerObservation ObserveStartAttempt(
        IBrokerStartAttempt startAttempt,
        BrokerObservation lastStateObservation,
        CancellationToken cancellationToken)
    {
        var hasExited = startAttempt.TryGetExitCode(out var exitCode);
        cancellationToken.ThrowIfCancellationRequested();
        if (!hasExited)
        {
            return new(
                BrokerObservationStatus.StartingOrLockOwned,
                "broker process " + startAttempt.ProcessId +
                " is starting; last observation: " + lastStateObservation.Detail);
        }

        if (exitCode != 0)
        {
            throw new BrokerBootstrapException(
                "broker_start_failed",
                "LocalAi broker process " + startAttempt.ProcessId +
                " exited with code " + exitCode +
                "; last observation: " + lastStateObservation.Detail);
        }

        return new(
            BrokerObservationStatus.StartingOrLockOwned,
            "startup process " + startAttempt.ProcessId +
            " exited successfully; lock owner did not publish compatible state; " +
            "last observation: " + lastStateObservation.Detail);
    }

    internal static BrokerProcessState? ReadState(string runtimeRoot)
    {
        var path = StatePath(runtimeRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BrokerProcessState>(
                File.ReadAllText(path),
                LocalAiJson.Strict);
        }
        catch (Exception exception) when (
            exception is JsonException or
            IOException or
            UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsRunning(BrokerProcessState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            return !process.HasExited &&
                   new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) ==
                   state.StartedAtUtc;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IBrokerStartAttempt StartAttempt(string executablePath, string arguments)
    {
        var process = Process.Start(CreateStartInfo(executablePath, arguments))
            ?? throw new InvalidOperationException("Could not start the LocalAi broker.");
        return new ProcessStartAttempt(process);
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string arguments) =>
        new(executablePath, arguments)
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

    private static string BuildArguments(string runtimeRoot, string? ollamaUrl)
    {
        var builder = new StringBuilder();
        builder.Append("serve --runtime ").Append(Quote(Path.GetFullPath(runtimeRoot)));
        if (!string.IsNullOrWhiteSpace(ollamaUrl))
        {
            builder.Append(" --ollama ").Append(Quote(ollamaUrl));
        }

        return builder.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private SemaphoreLease EnterSemaphore(
        long startupTimestamp,
        CancellationToken cancellationToken)
    {
        var semaphore = new Semaphore(1, 1, _startupSemaphoreName);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = RemainingStartupBudget(startupTimestamp);
                var waitSlice = remaining <= TimeSpan.Zero
                    ? TimeSpan.Zero
                    : remaining < MaximumSemaphoreWaitSlice
                        ? remaining
                        : MaximumSemaphoreWaitSlice;
                if (semaphore.WaitOne(waitSlice))
                {
                    return new SemaphoreLease(semaphore);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (remaining <= TimeSpan.Zero ||
                    RemainingStartupBudget(startupTimestamp) <= TimeSpan.Zero)
                {
                    throw new BrokerBootstrapException(
                        "broker_start_timeout",
                        $"LocalAi broker did not acquire the startup semaphore within " +
                        $"{_startupTimeout}; phase: startup semaphore; " +
                        "last observation: startup semaphore contention.");
                }
            }
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    private TimeSpan RemainingStartupBudget(long startupTimestamp)
    {
        var remaining =
            _startupTimeout - _timeProvider.GetElapsedTime(startupTimestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void ThrowIfStartupTimedOut(long startupTimestamp, string lastObservation)
    {
        if (RemainingStartupBudget(startupTimestamp) > TimeSpan.Zero)
        {
            return;
        }

        throw new BrokerBootstrapException(
            "broker_start_timeout",
            $"LocalAi broker did not become ready within {_startupTimeout}; " +
            $"last observation: {lastObservation}.");
    }

    private static string CreateSemaphoreName(string runtimeRoot)
    {
        var normalized = OperatingSystem.IsWindows()
            ? runtimeRoot.ToUpperInvariant()
            : runtimeRoot;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return (OperatingSystem.IsWindows() ? @"Global\" : string.Empty) +
               "LocalAi.Broker.Startup." + hash;
    }

    private sealed class SemaphoreLease(Semaphore semaphore) : IDisposable
    {
        public void Dispose()
        {
            semaphore.Release();
            semaphore.Dispose();
        }
    }

    private sealed class LegacyStartAttempt(int processId) : IBrokerStartAttempt
    {
        public int ProcessId { get; } = processId;

        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = default;
            return false;
        }

        public void Dispose()
        {
        }
    }

}

internal sealed class ProcessStartAttempt : IBrokerStartAttempt
{
    private readonly Process _process;

    public ProcessStartAttempt(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        ProcessId = process.Id;
    }

    public int ProcessId { get; }

    public bool TryGetExitCode(out int exitCode)
    {
        try
        {
            if (!_process.HasExited)
            {
                exitCode = default;
                return false;
            }

            exitCode = _process.ExitCode;
            return true;
        }
        catch (InvalidOperationException)
        {
            exitCode = default;
            return false;
        }
        catch (Win32Exception)
        {
            exitCode = default;
            return false;
        }
    }

    public void Dispose() => _process.Dispose();
}
