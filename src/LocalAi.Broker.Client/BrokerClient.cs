using System.Text.Json;
using LocalAi.Broker.Client.Resources;
using LocalAi.Contracts;

namespace LocalAi.Broker.Client;

public interface IBrokerClient
{
    Task<LocalJobResult<T>> ExecuteAsync<T>(
        LocalJobRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class BrokerClient : IBrokerClient
{
    private readonly DurableQueue _queue;
    private readonly IBrokerProcess _process;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;

    public BrokerClient(
        DurableQueue queue,
        IBrokerProcess process,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _delay = delay ?? Task.Delay;
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
    }

    public async Task<LocalJobResult<T>> ExecuteAsync<T>(
        LocalJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _process.EnsureRunningAsync(cancellationToken);
        var queued = await _queue.EnqueueAsync(request, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return await AwaitAsync<T>(queued.JobId, timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested &&
                _process.ReadBackendState() is { Reachable: false } backend)
        {
            // The job never ran, and the reason is not in the job. Reporting the wait as a bare
            // cancellation left "the tool hung and gave up", which nobody can act on.
            throw new BrokerBackendUnreachableException(backend.Endpoint);
        }
    }

    private async Task<LocalJobResult<T>> AwaitAsync<T>(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = await _queue.GetDiagnosticAsync(jobId, cancellationToken)
                ?? throw new BrokerProtocolException(
                    $"Broker job '{jobId}' disappeared from durable state.");

            switch (diagnostic.State)
            {
                case LocalJobState.Succeeded:
                {
                    var response = await _queue.ReadResponseAsync(jobId, cancellationToken)
                        ?? throw new BrokerProtocolException(
                            $"Broker job '{jobId}' succeeded without a response.");
                    try
                    {
                        var envelope = response.Body.Deserialize<BrokerResponseEnvelope>(
                            LocalAiJson.Strict)
                            ?? throw new BrokerProtocolException(
                                $"Broker job '{jobId}' returned an empty response.");
                        var value = envelope.Value.Deserialize<T>(LocalAiJson.Strict)
                            ?? throw new BrokerProtocolException(
                                $"Broker job '{jobId}' returned an empty value.");
                        if (envelope.Receipt.JobId != jobId)
                        {
                            throw new BrokerProtocolException(
                                $"Broker job '{jobId}' returned a mismatched receipt.");
                        }

                        return new LocalJobResult<T>(value, envelope.Receipt);
                    }
                    catch (JsonException exception)
                    {
                        throw new BrokerProtocolException(
                            $"Broker job '{jobId}' returned an invalid response.",
                            exception);
                    }
                }

                case LocalJobState.Failed:
                    throw new BrokerJobFailedException(
                        jobId,
                        diagnostic.FailureCode ?? "UnknownFailure");

                case LocalJobState.Cancelled:
                    throw new BrokerJobCancelledException(jobId);

                case LocalJobState.Queued:
                case LocalJobState.Running:
                    await _delay(_pollInterval, cancellationToken);
                    break;

                default:
                    throw new BrokerProtocolException(
                        $"Broker job '{jobId}' has unsupported state '{diagnostic.State}'.");
            }
        }
    }
}

/// <summary>
/// The job never ran because the model backend was not answering.
///
/// Distinct from a cancellation, which is what this used to be: the wait ran out while the job
/// sat in the queue, and the caller was told only that it had been cancelled. The broker is
/// right to keep waiting -- a backend that is down at boot usually comes up -- but somebody has
/// to be told why nothing happened.
/// </summary>
public sealed class BrokerBackendUnreachableException(string endpoint)
    : InvalidOperationException(
        $"Ollama is not answering at {endpoint}, so the job never ran. Start Ollama and try " +
        "again; queued work is kept and runs as soon as it answers.")
{
    public string Endpoint { get; } = endpoint;
}

public sealed class BrokerJobFailedException(Guid jobId, string failureCode)
    : InvalidOperationException(BrokerClientText.BrokerJobFailed(jobId, failureCode))
{
    public Guid JobId { get; } = jobId;

    public string FailureCode { get; } = failureCode;
}

public sealed class BrokerJobCancelledException(Guid jobId)
    : OperationCanceledException($"Broker job '{jobId}' was cancelled.")
{
    public Guid JobId { get; } = jobId;
}

public sealed class BrokerProtocolException : InvalidOperationException
{
    public BrokerProtocolException(string message)
        : base(message)
    {
    }

    public BrokerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
