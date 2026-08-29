using System.ComponentModel;
using System.Diagnostics;
using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// Starts Ollama once, when a model call finds it down.
///
/// Three limits, all of them deliberate.
///
/// **Only the executable an installation recorded**, never one resolved now. A path found at the
/// moment of use is how a background process ends up running whatever answers to the name, and
/// Ollama installs into a directory the user can write to, so the ACL check that guards the
/// winget executable would reject an ordinary installation rather than protect one. No record
/// means nothing is started.
///
/// **Only a loopback endpoint.** A broker pointed at another machine cannot start what it does
/// not own, and pretending otherwise would replace a clear message with a confusing one.
///
/// **Only once per broker.** Somebody who closed Ollama on purpose should not have it reappear
/// on every retry, and a start that did not help will not help the second time either.
/// </summary>
public sealed class BackendStarter
{
    private readonly Uri _endpoint;
    private readonly OllamaLaunchRecordStore _records;
    private readonly Action<string> _report;
    private readonly Action<string> _start;
    private int _attempted;

    /// <param name="start">
    /// How the recorded executable is run. Injected so a test can see what would be launched
    /// without launching it; by default a detached <c>ollama serve</c>, because the broker wants
    /// the API rather than the tray application.
    /// </param>
    public BackendStarter(
        Uri endpoint,
        OllamaLaunchRecordStore records,
        Action<string> report,
        Action<string>? start = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _start = start ?? StartDetached;
    }

    /// <summary>
    /// Whether a start has already been attempted, so a caller can say that the one attempt is
    /// spent rather than implying another is coming.
    /// </summary>
    public bool HasAttempted => Volatile.Read(ref _attempted) == 1;

    public void OnDiagnostic(BrokerHostDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!string.Equals(
                diagnostic.ExceptionType,
                nameof(HttpRequestException),
                StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.Exchange(ref _attempted, 1) == 1)
        {
            return;
        }

        Start();
    }

    private void Start()
    {
        if (!_endpoint.IsLoopback)
        {
            _report(
                $"Ollama is not answering at {_endpoint}, which is not this machine, so it " +
                "cannot be started from here.");
            return;
        }

        if (_records.Read() is not { } record)
        {
            _report(
                "Ollama is not answering and no verified installation of it was recorded, so " +
                "it will not be started. Run the LocalAi installer once to record one.");
            return;
        }

        try
        {
            _start(record.ExecutablePath);
            _report(
                $"Ollama was not answering; started {record.ExecutablePath}. Queued work runs " +
                "as soon as it answers.");
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException or
                ObjectDisposedException)
        {
            _report(
                $"Ollama could not be started from {record.ExecutablePath}: " +
                exception.Message);
        }
    }

    private static void StartDetached(string executablePath)
    {
        using var started = Process.Start(new ProcessStartInfo(executablePath)
        {
            ArgumentList = { "serve" },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (started is null)
        {
            throw new InvalidOperationException("The process did not start.");
        }
    }
}
