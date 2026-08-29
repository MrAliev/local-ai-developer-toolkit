using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Starting Ollama when a model call finds it down is the friendly half of the answer. The
/// unfriendly half is what it must refuse to start, and that is most of what is tested here: a
/// background process that launches whatever answers to a name is the hole this project closed
/// for winget, and it must not be reopened for Ollama.
/// </summary>
public sealed class BackendStarterTests : IDisposable
{
    private const string Recorded = @"C:\Users\someone\AppData\Local\Programs\Ollama\ollama.exe";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-backend-starter-" + Guid.NewGuid().ToString("N"));

    public BackendStarterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void An_unreachable_local_backend_starts_the_recorded_executable()
    {
        RecordPath(Recorded);
        var started = new List<string>();
        var starter = Starter("http://127.0.0.1:11434/", started, out var reports);

        starter.OnDiagnostic(Diagnostic(nameof(HttpRequestException)));

        Assert.Equal([Recorded], started);
        Assert.Contains(reports, line => line.Contains("started", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole reason the path is recorded rather than looked up. Nothing is started on a
    /// machine where no installation established what to start.
    /// </summary>
    [Fact]
    public void Nothing_is_started_when_no_installation_recorded_anything()
    {
        var started = new List<string>();
        var starter = Starter("http://127.0.0.1:11434/", started, out var reports);

        starter.OnDiagnostic(Diagnostic(nameof(HttpRequestException)));

        Assert.Empty(started);
        Assert.Contains(reports, line => line.Contains("was recorded", StringComparison.Ordinal));
    }

    /// <summary>
    /// A broker pointed at another machine cannot start what it does not own, and saying so
    /// beats a confusing failure.
    /// </summary>
    [Fact]
    public void A_backend_on_another_machine_is_not_started()
    {
        RecordPath(Recorded);
        var started = new List<string>();
        var starter = Starter("http://gpu-box.local:11434/", started, out var reports);

        starter.OnDiagnostic(Diagnostic(nameof(HttpRequestException)));

        Assert.Empty(started);
        Assert.Contains(
            reports,
            line => line.Contains("not this machine", StringComparison.Ordinal));
    }

    /// <summary>
    /// Somebody who closed Ollama on purpose should not have it reappear on every retry, and a
    /// start that did not help will not help the second time either.
    /// </summary>
    [Fact]
    public void Ollama_is_started_at_most_once()
    {
        RecordPath(Recorded);
        var started = new List<string>();
        var starter = Starter("http://127.0.0.1:11434/", started, out _);

        starter.OnDiagnostic(Diagnostic(nameof(HttpRequestException)));
        starter.OnDiagnostic(Diagnostic(nameof(HttpRequestException)));
        starter.OnDiagnostic(Diagnostic(nameof(HttpRequestException)));

        Assert.Single(started);
        Assert.True(starter.HasAttempted);
    }

    /// <summary>
    /// Only a backend that would not answer. Every other failure has its own cause, and starting
    /// a second Ollama would add one.
    /// </summary>
    [Theory]
    [InlineData("IOException")]
    [InlineData("InvalidOperationException")]
    [InlineData("TaskCanceledException")]
    public void Another_kind_of_failure_starts_nothing(string exceptionType)
    {
        RecordPath(Recorded);
        var started = new List<string>();
        var starter = Starter("http://127.0.0.1:11434/", started, out _);

        starter.OnDiagnostic(Diagnostic(exceptionType));

        Assert.Empty(started);
        Assert.False(starter.HasAttempted);
    }

    [Fact]
    public void A_start_that_fails_is_reported_rather_than_thrown()
    {
        RecordPath(Recorded);
        var reports = new List<string>();
        var starter = new BackendStarter(
            new Uri("http://127.0.0.1:11434/"),
            new OllamaLaunchRecordStore(_root),
            reports.Add,
            _ => throw new IOException("the file is in use"));

        var exception = Xunit.Record.Exception(
            () => starter.OnDiagnostic(Diagnostic(nameof(HttpRequestException))));

        Assert.Null(exception);
        Assert.Contains(
            reports,
            line => line.Contains("could not be started", StringComparison.Ordinal));
    }

    private BackendStarter Starter(
        string endpoint,
        List<string> started,
        out List<string> reports)
    {
        var lines = new List<string>();
        reports = lines;
        return new BackendStarter(
            new Uri(endpoint),
            new OllamaLaunchRecordStore(_root),
            lines.Add,
            started.Add);
    }

    private void RecordPath(string executablePath) =>
        new OllamaLaunchRecordStore(_root).Save(executablePath, "0.5.0");

    private static BrokerHostDiagnostic Diagnostic(string exceptionType) =>
        new(Guid.Empty, "worker", Guid.Empty, "schedule", exceptionType);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
