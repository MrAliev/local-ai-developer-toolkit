using System.Diagnostics;
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
    private readonly string _executablePath;
    private readonly string _runtimeRoot;
    private readonly Func<string, BrokerProcessState?> _readState;
    private readonly Func<BrokerProcessState, bool> _isRunning;
    private readonly Func<string, string, int> _start;
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
            Start,
            timeProvider ?? TimeProvider.System,
            Task.Delay,
            startupTimeout,
            BuildArguments(runtimeRoot, ollamaUrl))
    {
    }

    public static BrokerProcess CreateDefault(string runtimeRoot)
    {
        var brokerAssembly = typeof(DurableQueue).Assembly.Location;
        var arguments =
            Quote(brokerAssembly) + " " + BuildArguments(runtimeRoot, null);
        return new BrokerProcess(
            "dotnet",
            runtimeRoot,
            ReadState,
            IsRunning,
            Start,
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
        string? arguments = null)
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
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        using var startupLock = await Task.Run(
            () => EnterSemaphore(cancellationToken),
            cancellationToken);
        if (IsHealthy(_readState(_runtimeRoot)))
        {
            return;
        }

        _start(_executablePath, _arguments);
        var deadline = _timeProvider.GetUtcNow() + _startupTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsHealthy(_readState(_runtimeRoot)))
            {
                return;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new TimeoutException(
                    $"LocalAi broker did not become ready within {_startupTimeout}.");
            }

            await _delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    public static string StatePath(string runtimeRoot) =>
        Path.Combine(Path.GetFullPath(runtimeRoot), "host.json");

    private bool IsHealthy(BrokerProcessState? state) =>
        state is { SchemaVersion: 1 } &&
        _isRunning(state) &&
        _timeProvider.GetUtcNow() - state.HeartbeatAtUtc <= TimeSpan.FromSeconds(5);

    private static BrokerProcessState? ReadState(string runtimeRoot)
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
        catch (JsonException)
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

    private static int Start(string executablePath, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(executablePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Could not start the LocalAi broker.");
        return process.Id;
    }

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

    private SemaphoreLease EnterSemaphore(CancellationToken cancellationToken)
    {
        var semaphore = new Semaphore(1, 1, _startupSemaphoreName);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (semaphore.WaitOne(TimeSpan.FromMilliseconds(100)))
                {
                    return new SemaphoreLease(semaphore);
                }
            }
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
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
}
