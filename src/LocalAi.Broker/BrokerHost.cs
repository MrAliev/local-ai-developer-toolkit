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
    private readonly ModelAwareScheduler? _scheduler;
    private readonly Func<
        IReadOnlyList<QueuedJobCandidate>,
        CancellationToken,
        Task<IReadOnlyList<ScheduledJobCandidate>>>? _scheduleMetadata;
    private readonly Func<string?> _residentModel;
    private readonly Action<LocalJobRequest, LocalRoutingReceipt?, TimeSpan> _durationObserver;
    private readonly Func<CancellationToken, Task> _idleUnload;
    private readonly TimeSpan _idleUnloadAfter;
    private DateTimeOffset _lastActivityAtUtc;
    private bool _idleUnloadIssued;

    public BrokerHost(
        IBrokerQueue queue,
        string workerId,
        Func<LocalJobRequest, CancellationToken, Task<BrokerExecutionResult>> executor,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? idleDelay = null,
        TimeSpan? idleInterval = null,
        TimeSpan? heartbeatInterval = null,
        Action<BrokerHostDiagnostic>? diagnostic = null,
        ModelAwareScheduler? scheduler = null,
        Func<
            IReadOnlyList<QueuedJobCandidate>,
            CancellationToken,
            Task<IReadOnlyList<ScheduledJobCandidate>>>? scheduleMetadata = null,
        Func<string?>? residentModel = null,
        Action<LocalJobRequest, LocalRoutingReceipt?, TimeSpan>? durationObserver = null,
        Func<CancellationToken, Task>? idleUnload = null,
        TimeSpan? idleUnloadAfter = null)
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
        _scheduler = scheduler;
        _scheduleMetadata = scheduleMetadata;
        _residentModel = residentModel ?? (() => null);
        _durationObserver = durationObserver ?? ((_, _, _) => { });
        _idleUnload = idleUnload ?? (_ => Task.CompletedTask);
        _idleUnloadAfter = idleUnloadAfter ?? TimeSpan.FromMinutes(30);
        _lastActivityAtUtc = clock.GetUtcNow();
        if ((_scheduler is null) != (_scheduleMetadata is null))
        {
            throw new ArgumentException(
                "Scheduler and scheduling metadata resolver must be supplied together.");
        }
        if (_idleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleInterval));
        }

        if (_idleUnloadAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleUnloadAfter));
        }

        if (_heartbeatInterval <= TimeSpan.Zero ||
            _heartbeatInterval >= queue.LeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }
    }

    public Task RunAsync(CancellationToken cancellationToken) =>
        RunAsync(cancellationToken, drainRequested: null);

    /// <summary>
    /// Runs until cancelled, or until <paramref name="drainRequested"/> asks it to stop taking
    /// work.
    ///
    /// The two are not the same thing and must not be conflated. Cancellation reaches into the
    /// running job and abandons it; draining does not touch it at all — it only stops the loop
    /// from leasing anything new, so the job in flight runs to completion and is reported
    /// normally before the process exits. Stopping a broker mid-inference to replace a binary
    /// is exactly the kind of thing that should never be the only option available.
    /// </summary>
    public async Task RunAsync(
        CancellationToken cancellationToken,
        Func<bool>? drainRequested)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drainRequested?.Invoke() == true)
            {
                return;
            }

            var lease = await LeaseNextAsync(cancellationToken);
            if (lease is null)
            {
                await TryUnloadIdleAsync(cancellationToken);
                await _idleDelay(_idleInterval, cancellationToken);
                continue;
            }

            _idleUnloadIssued = false;
            await RunAttemptAsync(lease, cancellationToken);
            _lastActivityAtUtc = _timeProvider.GetUtcNow();
        }
    }

    private async Task TryUnloadIdleAsync(CancellationToken cancellationToken)
    {
        if (_idleUnloadIssued ||
            _timeProvider.GetUtcNow() - _lastActivityAtUtc < _idleUnloadAfter ||
            _queue is not ISelectableBrokerQueue selectable)
        {
            return;
        }

        var queued = await selectable.ListQueuedAsync(cancellationToken);
        if (queued.Count > 0)
        {
            return;
        }

        await _idleUnload(cancellationToken);
        _idleUnloadIssued = true;
    }

    private async Task<LeasedJob?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        if (_scheduler is null ||
            _scheduleMetadata is null ||
            _queue is not ISelectableBrokerQueue selectable)
        {
            return await _queue.LeaseNextAsync(_workerId, cancellationToken);
        }

        var queued = await selectable.ListQueuedAsync(cancellationToken);
        var scheduled = await _scheduleMetadata(queued, cancellationToken);
        var decision = _scheduler.Decide(
            scheduled,
            _residentModel());
        if (decision.WaitUntilUtc is { } waitUntil)
        {
            var delay = waitUntil - _timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await _idleDelay(delay, cancellationToken);
            }

            return null;
        }

        return decision.JobId is { } jobId
            ? await selectable.TryLeaseAsync(jobId, _workerId, cancellationToken)
            : null;
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
            try
            {
                _durationObserver(
                    lease.Request,
                    result.Routing,
                    completedAtUtc - executionStartedAtUtc);
            }
            catch (Exception exception)
            {
                Report(lease, "duration-observe", exception);
            }

            var envelope = new BrokerResponseEnvelope(
                result.Body,
                ReceiptFactory.Create(
                    lease.Request,
                    executionStartedAtUtc,
                    completedAtUtc,
                    result.Routing));
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

public sealed record BrokerExecutionResult(
    JsonElement Body,
    LocalRoutingReceipt? Routing = null);

public sealed record BrokerHostDiagnostic(
    Guid JobId,
    string WorkerId,
    Guid LeaseId,
    string Operation,
    string ExceptionType);
