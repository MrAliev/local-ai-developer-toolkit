using System.Text.Json;
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
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var diagnostic = await _queue.GetDiagnosticAsync(queued.JobId, timeout.Token)
                ?? throw new BrokerProtocolException(
                    $"Broker job '{queued.JobId}' disappeared from durable state.");

            switch (diagnostic.State)
            {
                case LocalJobState.Succeeded:
                {
                    var response = await _queue.ReadResponseAsync(queued.JobId, timeout.Token)
                        ?? throw new BrokerProtocolException(
                            $"Broker job '{queued.JobId}' succeeded without a response.");
                    try
                    {
                        var envelope = response.Body.Deserialize<BrokerResponseEnvelope>(
                            LocalAiJson.Strict)
                            ?? throw new BrokerProtocolException(
                                $"Broker job '{queued.JobId}' returned an empty response.");
                        var value = envelope.Value.Deserialize<T>(LocalAiJson.Strict)
                            ?? throw new BrokerProtocolException(
                                $"Broker job '{queued.JobId}' returned an empty value.");
                        if (envelope.Receipt.JobId != queued.JobId)
                        {
                            throw new BrokerProtocolException(
                                $"Broker job '{queued.JobId}' returned a mismatched receipt.");
                        }

                        return new LocalJobResult<T>(value, envelope.Receipt);
                    }
                    catch (JsonException exception)
                    {
                        throw new BrokerProtocolException(
                            $"Broker job '{queued.JobId}' returned an invalid response.",
                            exception);
                    }
                }

                case LocalJobState.Failed:
                    throw new BrokerJobFailedException(
                        queued.JobId,
                        diagnostic.FailureCode ?? "UnknownFailure");

                case LocalJobState.Cancelled:
                    throw new BrokerJobCancelledException(queued.JobId);

                case LocalJobState.Queued:
                case LocalJobState.Running:
                    await _delay(_pollInterval, timeout.Token);
                    break;

                default:
                    throw new BrokerProtocolException(
                        $"Broker job '{queued.JobId}' has unsupported state '{diagnostic.State}'.");
            }
        }
    }
}

public sealed class BrokerJobFailedException(Guid jobId, string failureCode)
    : InvalidOperationException($"Broker job '{jobId}' failed with '{failureCode}'.")
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
