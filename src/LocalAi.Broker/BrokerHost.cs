using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class BrokerHost
{
    private readonly IBrokerQueue _queue;
    private readonly string _workerId;
    private readonly Func<LocalJobRequest, IJobProgress, CancellationToken,
        Task<BrokerExecutionResult>> _executor;
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
    private readonly Func<CancellationToken, Task<BackendProbeResult>>? _backendProbe;
    private readonly BackendWatchdogPolicy _watchdogPolicy;
    private DateTimeOffset _lastActivityAtUtc;
    private bool _idleUnloadIssued;

    public BrokerHost(
        IBrokerQueue queue,
        string workerId,
        Func<LocalJobRequest, IJobProgress, CancellationToken, Task<BrokerExecutionResult>>
            executor,
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
        TimeSpan? idleUnloadAfter = null,
        Func<CancellationToken, Task<BackendProbeResult>>? backendProbe = null,
        BackendWatchdogPolicy? watchdogPolicy = null)
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
        _idleUnloadAfter = idleUnloadAfter ?? TimeSpan.Zero;
        _backendProbe = backendProbe;
        _watchdogPolicy = watchdogPolicy ?? BackendWatchdogPolicy.Default;
        _watchdogPolicy.Validate();
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

        if (_idleUnloadAfter < TimeSpan.Zero)
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
        var residentModel = _residentModel();
        if (queued.Count > 0 && string.IsNullOrWhiteSpace(residentModel))
        {
            return;
        }

        if (queued.Count > 0)
        {
            // Without routing metadata the host cannot prove that the queued work targets a
            // different model, so retain the resident model conservatively.
            if (_scheduleMetadata is null)
            {
                return;
            }

            var scheduled = await TryScheduleAsync(queued, cancellationToken);
            if (scheduled is null ||
                scheduled.Any(candidate =>
                    string.Equals(
                        candidate.Model,
                        residentModel,
                        StringComparison.Ordinal)))
            {
                return;
            }
        }

        try
        {
            await _idleUnload(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Unloading talks to the backend too, and a backend that cannot be reached is one
            // that is holding no model to unload. Leave _idleUnloadIssued alone so the next
            // idle turn tries again.
            Report("idle-unload", exception);
            return;
        }

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
        var scheduled = await TryScheduleAsync(queued, cancellationToken);
        if (scheduled is null)
        {
            return null;
        }

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
        DateTimeOffset? lastProbeAtUtc = null;
        var consecutiveUnhealthyProbes = 0;
        Task<BrokerExecutionResult> execution;
        try
        {
            execution = _executor(
                lease.Request,
                new LeasedJobProgress(_queue, lease),
                attemptCancellation.Token);
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

                var now = _timeProvider.GetUtcNow();
                if (_backendProbe is null ||
                    now - executionStartedAtUtc < _watchdogPolicy.SilenceBeforeProbe ||
                    lastProbeAtUtc is { } previousProbe &&
                    now - previousProbe < _watchdogPolicy.ProbeInterval)
                {
                    continue;
                }

                lastProbeAtUtc = now;
                var probe = await ProbeBackendAsync(hostCancellation);
                if (probe.Liveness != BackendLiveness.Unhealthy)
                {
                    consecutiveUnhealthyProbes = 0;
                    continue;
                }

                consecutiveUnhealthyProbes++;
                Report(
                    lease,
                    $"watchdog-unhealthy:{probe.Code}",
                    new BackendUnavailableException(probe.Code));
                if (consecutiveUnhealthyProbes < _watchdogPolicy.RequiredUnhealthyProbes)
                {
                    continue;
                }

                var backendFailure = new BackendUnavailableException(probe.Code);
                attemptCancellation.Cancel();
                await ObserveCancellationAsync(execution);
                await TryFailAsync(lease, backendFailure);
                return;
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

    private async Task<BackendProbeResult> ProbeBackendAsync(
        CancellationToken hostCancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);
        timeout.CancelAfter(_watchdogPolicy.ProbeTimeout);
        try
        {
            return await _backendProbe!(timeout.Token);
        }
        catch (OperationCanceledException) when (!hostCancellation.IsCancellationRequested)
        {
            return BackendProbeResult.Inconclusive("probe_timeout");
        }
        catch (Exception exception)
        {
            return BackendProbeResult.Inconclusive(exception.GetType().Name);
        }
    }

    private async Task TryFailAsync(LeasedJob lease, Exception executionException)
    {
        // Why the job failed, said once into the log that exists for exactly this and recorded
        // beside the job for whoever awaits it. Until now the type was the whole account, and a
        // backend that refused a batch in a sentence of its own had that sentence dropped three
        // times over: here, in the durable state, and again in the exception the client threw.
        Report(lease, "execute", executionException, executionException.Message);
        try
        {
            await _queue.FailAsync(
                lease.Request.JobId,
                _workerId,
                lease.LeaseId,
                executionException.GetType().Name,
                executionException.Message,
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

    /// <summary>
    /// Deciding what to lease reaches the backend, and the backend is allowed to be down: a boot
    /// race, a service restart, a user who closed the tray app.
    ///
    /// Until this returned null instead of throwing, that exception travelled out of
    /// LeaseNextAsync, out of RunAsync and off the top of the process — a Windows crash dialog on
    /// every boot where the MCP servers start before Ollama does. Nothing has been leased at this
    /// point, so the turn is skipped and the queued work waits for a backend that answers. The
    /// transport has already spent its own retries getting here, which is what paces the waiting.
    /// </summary>
    private async Task<IReadOnlyList<ScheduledJobCandidate>?> TryScheduleAsync(
        IReadOnlyList<QueuedJobCandidate> queued,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _scheduleMetadata!(queued, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Report("schedule", exception);
            return null;
        }
    }

    private void Report(
        LeasedJob lease,
        string operation,
        Exception exception,
        string? reason = null) =>
        Report(lease.Request.JobId, lease.LeaseId, operation, exception, reason);

    /// <summary>
    /// Reports a failure that happened before any job was leased, so there is no job and no
    /// lease to name.
    /// </summary>
    private void Report(string operation, Exception exception) =>
        Report(Guid.Empty, Guid.Empty, operation, exception);

    /// <param name="reason">
    /// What the failure said, for the one operation where that is the answer somebody is
    /// looking for: the job's own execution. Every other operation here is this broker's
    /// plumbing — a heartbeat that could not be written, a state file that would not move —
    /// and their messages carry filesystem paths rather than explanations, which is why this
    /// log has recorded types alone since it was written.
    /// </param>
    private void Report(
        Guid jobId,
        Guid leaseId,
        string operation,
        Exception exception,
        string? reason = null)
    {
        try
        {
            _diagnostic(new BrokerHostDiagnostic(
                jobId,
                _workerId,
                leaseId,
                operation,
                exception.GetType().Name,
                reason));
        }
        catch
        {
            // A diagnostic sink is observational and cannot own broker liveness.
        }
    }
}

/// <summary>
/// The sink the host hands to the executor: the one job it holds a lease for, and no
/// other. Reporting is best-effort by construction — a position is an encouragement, and
/// a queue that refused one must never turn a running download into a failed job.
/// </summary>
internal sealed class LeasedJobProgress(IBrokerQueue queue, LeasedJob lease) : IJobProgress
{
    public Task ReportAsync(
        JobProgress progress,
        CancellationToken cancellationToken = default) =>
        queue.ReportProgressAsync(
            lease.Request.JobId,
            lease.WorkerId,
            lease.LeaseId,
            progress,
            cancellationToken);
}

public sealed record BrokerExecutionResult(
    JsonElement Body,
    LocalRoutingReceipt? Routing = null);

public sealed record BrokerHostDiagnostic(
    Guid JobId,
    string WorkerId,
    Guid LeaseId,
    string Operation,
    string ExceptionType,
    string? Reason = null);
