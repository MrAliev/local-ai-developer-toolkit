using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// A job waits in the queue while Ollama is down. That is right — the broker keeps retrying, and
/// a backend that is down at boot usually comes up a minute later — but when the client's own
/// wait ran out it reported a bare cancellation. "The tool hung and gave up" is not something
/// anybody can act on.
///
/// The broker's own stderr says it plainly, and nobody sees that: it is started detached, with no
/// console and nothing redirected. So the fact has to travel in the state the client can read.
/// </summary>
public sealed class BackendUnreachableReportingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-backend-report-" + Guid.NewGuid().ToString("N"));

    public BackendUnreachableReportingTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task A_wait_that_runs_out_while_the_backend_is_down_says_so()
    {
        var queue = new DurableQueue(_root);
        var client = new BrokerClient(
            queue,
            new StaticProcess(new BrokerBackendState(false, "http://127.0.0.1:11434/")),
            delay: (_, token) => Task.Delay(1, token),
            timeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(5));

        var exception = await Assert.ThrowsAsync<BrokerBackendUnreachableException>(() =>
            client.ExecuteAsync<object>(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("http://127.0.0.1:11434/", exception.Endpoint);
        // The address and the move to make, not just that something went wrong.
        Assert.Contains("127.0.0.1:11434", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Start Ollama", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A backend that is answering means the wait ran out for some other reason, and claiming
    /// Ollama is down would send the reader somewhere there is nothing to find.
    /// </summary>
    [Fact]
    public async Task A_wait_that_runs_out_while_the_backend_answers_stays_a_cancellation()
    {
        var queue = new DurableQueue(_root);
        var client = new BrokerClient(
            queue,
            new StaticProcess(new BrokerBackendState(true, "http://127.0.0.1:11434/")),
            delay: (_, token) => Task.Delay(1, token),
            timeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExecuteAsync<object>(Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An older broker publishes no backend state at all, and a client meeting one must not
    /// invent a diagnosis for it.
    /// </summary>
    [Fact]
    public async Task A_broker_that_says_nothing_about_its_backend_is_not_spoken_for()
    {
        var queue = new DurableQueue(_root);
        var client = new BrokerClient(
            queue,
            new StaticProcess(null),
            delay: (_, token) => Task.Delay(1, token),
            timeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExecuteAsync<object>(Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The caller's own cancellation is theirs, and must not come back wearing a diagnosis about
    /// Ollama.
    /// </summary>
    [Fact]
    public async Task A_caller_who_cancels_is_not_told_about_the_backend()
    {
        var queue = new DurableQueue(_root);
        var client = new BrokerClient(
            queue,
            new StaticProcess(new BrokerBackendState(false, "http://127.0.0.1:11434/")),
            delay: (_, token) => Task.Delay(1, token),
            pollInterval: TimeSpan.FromMilliseconds(5));
        using var caller = new CancellationTokenSource();

        var running = client.ExecuteAsync<object>(Request(), caller.Token);
        await caller.CancelAsync();

        var exception = await Record.ExceptionAsync(() => running);
        Assert.IsNotType<BrokerBackendUnreachableException>(exception);
    }

    private static LocalJobRequest Request() =>
        LocalJobRequestFactory.CreateEmbed(
            "backend-report",
            LocalJobPriority.Foreground,
            "test-model",
            ["input"]);

    private sealed class StaticProcess(BrokerBackendState? backend) : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public BrokerBackendState? ReadBackendState() => backend;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
