using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class BrokerHost
{
    private readonly IBrokerQueue _queue;
    private readonly string _workerId;
    private readonly Func<LocalJobRequest, CancellationToken, Task<BrokerExecutionResult>> _executor;
    private readonly Func<TimeSpan, CancellationToken, Task> _idleDelay;
    private readonly TimeSpan _idleInterval;
    private readonly TimeSpan _heartbeatInterval;
    private readonly Action<BrokerHostDiagnostic> _diagnostic;
    private readonly TimeProvider _timeProvider;

    public BrokerHost(
        IBrokerQueue queue,
        string workerId,
        Func<LocalJobRequest, CancellationToken, Task<BrokerExecutionResult>> executor,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? idleDelay = null,
        TimeSpan? idleInterval = null,
        TimeSpan? heartbeatInterval = null,
        Action<BrokerHostDiagnostic>? diagnostic = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        _workerId = workerId;
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        var clock = timeProvider ?? TimeProvider.System;
        _timeProvider = clock;
        _idleDelay = idleDelay ?? ((delay, token) => Task.Delay(delay, clock, token));
        _idleInterval = idleInterval ?? TimeSpan.FromMilliseconds(100);
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromTicks(queue.LeaseDuration.Ticks / 3);
        _diagnostic = diagnostic ?? (_ => { });
        if (_idleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleInterval));
        }

        if (_heartbeatInterval <= TimeSpan.Zero ||
            _heartbeatInterval >= queue.LeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = await _queue.LeaseNextAsync(_workerId, cancellationToken);
            if (lease is null)
            {
                await _idleDelay(_idleInterval, cancellationToken);
                continue;
            }

            await RunAttemptAsync(lease, cancellationToken);
        }
    }

    private async Task RunAttemptAsync(LeasedJob lease, CancellationToken hostCancellation)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);
        var executionStartedAtUtc = _timeProvider.GetUtcNow();
        Task<BrokerExecutionResult> execution;
        try
        {
            execution = _executor(lease.Request, attemptCancellation.Token);
        }
        catch (OperationCanceledException) when (hostCancellation.IsCancellationRequested)
        {
            attemptCancellation.Cancel();
            await TryCancelAsync(lease);
            throw;
        }
        catch (Exception exception)
        {
            await TryFailAsync(lease, exception);
            return;
        }

        try
        {
            while (!execution.IsCompleted)
            {
                var heartbeatDelay = _idleDelay(_heartbeatInterval, hostCancellation);
                var completed = await Task.WhenAny(execution, heartbeatDelay);
                if (completed == execution)
                {
                    break;
                }

                await heartbeatDelay;
                await _queue.HeartbeatAsync(
                    lease.Request.JobId,
                    _workerId,
                    lease.LeaseId,
                    hostCancellation);
            }
        }
        catch (LeaseLostException exception)
        {
            attemptCancellation.Cancel();
            await ObserveCancellationAsync(execution);
            Report(lease, "heartbeat", exception);
            return;
        }
        catch (OperationCanceledException) when (hostCancellation.IsCancellationRequested)
        {
            attemptCancellation.Cancel();
            await ObserveCancellationAsync(execution);
            await TryCancelAsync(lease);
            throw;
        }
        catch (Exception exception)
        {
            attemptCancellation.Cancel();
            Report(lease, "heartbeat", exception);
            await ObserveCancellationAsync(execution);
            throw;
        }

        BrokerExecutionResult result;
        try
        {
            result = await execution;
        }
        catch (OperationCanceledException) when (hostCancellation.IsCancellationRequested)
        {
            attemptCancellation.Cancel();
            await TryCancelAsync(lease);
            throw;
        }
        catch (Exception exception)
        {
            await TryFailAsync(lease, exception);
            return;
        }

        try
        {
            var completedAtUtc = _timeProvider.GetUtcNow();
            var envelope = new BrokerResponseEnvelope(
                result.Body,
                ReceiptFactory.Create(
                    lease.Request,
                    executionStartedAtUtc,
                    completedAtUtc));
            await _queue.CompleteAsync(
                lease.Request.JobId,
                _workerId,
                lease.LeaseId,
                JsonSerializer.SerializeToElement(envelope, LocalAiJson.Strict),
                CancellationToken.None);
        }
        catch (LeaseLostException exception)
        {
            attemptCancellation.Cancel();
            Report(lease, "complete", exception);
        }
        catch (Exception exception)
        {
            Report(lease, "complete", exception);
        }
    }

    private async Task TryFailAsync(LeasedJob lease, Exception executionException)
    {
        try
        {
            await _queue.FailAsync(
                lease.Request.JobId,
                _workerId,
                lease.LeaseId,
                executionException.GetType().Name,
                CancellationToken.None);
        }
        catch (LeaseLostException exception)
        {
            Report(lease, "fail", exception);
        }
        catch (Exception exception)
        {
            Report(lease, "fail", exception);
        }
    }

    private async Task TryCancelAsync(LeasedJob lease)
    {
        try
        {
            await _queue.CancelAsync(
                lease.Request.JobId,
                _workerId,
                lease.LeaseId,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Report(lease, "cancel", exception);
        }
    }

    private static async Task ObserveCancellationAsync(Task execution)
    {
        try
        {
            await execution;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void Report(LeasedJob lease, string operation, Exception exception)
    {
        try
        {
            _diagnostic(new BrokerHostDiagnostic(
                lease.Request.JobId,
                _workerId,
                lease.LeaseId,
                operation,
                exception.GetType().Name));
        }
        catch
        {
            // A diagnostic sink is observational and cannot own broker liveness.
        }
    }
}

public sealed record BrokerExecutionResult(JsonElement Body);

public sealed record BrokerHostDiagnostic(
    Guid JobId,
    string WorkerId,
    Guid LeaseId,
    string Operation,
    string ExceptionType);
