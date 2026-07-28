using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class DurableQueue : IBrokerQueue
{
    private static JsonSerializerOptions JsonOptions => LocalAiJson.Strict;

    private readonly string _jobsRoot;
    private readonly string _archiveRoot;
    private readonly string _stagingRoot;
    private readonly string _quarantineRoot;
    private readonly string _sequencePath;
    private readonly TimeProvider _timeProvider;
    private readonly string _mutexName;

    public DurableQueue(
        string runtimeRoot,
        TimeProvider? timeProvider = null,
        TimeSpan? leaseDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        RuntimeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        _jobsRoot = Path.Combine(RuntimeRoot, "jobs");
        _archiveRoot = Path.Combine(RuntimeRoot, "archive");
        _stagingRoot = Path.Combine(RuntimeRoot, "staging");
        _quarantineRoot = Path.Combine(RuntimeRoot, "quarantine");
        _sequencePath = Path.Combine(RuntimeRoot, "sequence.json");
        _timeProvider = timeProvider ?? TimeProvider.System;
        LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2);
        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        Directory.CreateDirectory(_jobsRoot);
        Directory.CreateDirectory(_archiveRoot);
        Directory.CreateDirectory(_stagingRoot);
        Directory.CreateDirectory(_quarantineRoot);
        _mutexName = CreateMutexName(RuntimeRoot);
    }

    public string RuntimeRoot { get; }

    public TimeSpan LeaseDuration { get; }

    public Task<EnqueueResult> EnqueueAsync(
        LocalJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var mutex = EnterMutex(cancellationToken);
        MaintainActiveRoot();
        foreach (var entry in ReadEntries())
        {
            if (entry.State.State is not (LocalJobState.Queued or LocalJobState.Running))
            {
                continue;
            }

            var existingRequest = ReadRequest(entry.Directory);
            if (string.Equals(
                existingRequest.DeduplicationKey,
                request.DeduplicationKey,
                StringComparison.Ordinal))
            {
                return Task.FromResult(new EnqueueResult(
                    existingRequest.JobId,
                    entry.State.Sequence,
                    JoinedExisting: true));
            }
        }

        var sequence = AllocateSequence();
        var jobDirectory = GetJobDirectory(request.JobId);
        if (Directory.Exists(jobDirectory) || Directory.Exists(GetArchiveDirectory(request.JobId)))
        {
            throw new InvalidOperationException($"Job '{request.JobId}' already exists.");
        }

        var stagingDirectory = Path.Combine(
            _stagingRoot,
            request.JobId.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(stagingDirectory);
        AtomicWriteJson(
            Path.Combine(stagingDirectory, "request.json"),
            new RequestDocument(1, request));
        var now = _timeProvider.GetUtcNow();
        var state = new JobStateDocument(
            1,
            request.JobId,
            sequence,
            request.Priority,
            LocalJobState.Queued,
            now,
            now,
            null,
            null,
            null,
            null,
            0,
            0,
            null);
        AtomicWriteJson(GetStatePath(stagingDirectory), state);
        Directory.Move(stagingDirectory, jobDirectory);
        return Task.FromResult(new EnqueueResult(request.JobId, sequence, JoinedExisting: false));
    }

    public Task<LeasedJob?> LeaseNextAsync(
        string workerId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkerId(workerId);
        cancellationToken.ThrowIfCancellationRequested();

        using var mutex = EnterMutex(cancellationToken);
        MaintainActiveRoot();
        var now = _timeProvider.GetUtcNow();
        RecoverExpiredLeases(now);
        var entries = ReadEntries();
        if (entries.Any(entry => entry.State.State == LocalJobState.Running))
        {
            return Task.FromResult<LeasedJob?>(null);
        }

        var next = entries
            .Where(entry => entry.State.State == LocalJobState.Queued)
            .OrderBy(entry => entry.State.Priority)
            .ThenBy(entry => entry.State.Sequence)
            .FirstOrDefault();
        if (next is null)
        {
            return Task.FromResult<LeasedJob?>(null);
        }

        var expires = now + LeaseDuration;
        var leaseId = Guid.NewGuid();
        var running = next.State with
        {
            State = LocalJobState.Running,
            UpdatedAtUtc = now,
            WorkerId = workerId,
            LeaseId = leaseId,
            LeaseExpiresAtUtc = expires,
            HeartbeatAtUtc = now,
            AttemptCount = next.State.AttemptCount + 1,
            FailureCode = null
        };
        WriteState(next.Directory, running);
        return Task.FromResult<LeasedJob?>(new LeasedJob(
            ReadRequest(next.Directory),
            running.Sequence,
            workerId,
            leaseId,
            expires,
            running.AttemptCount,
            running.RecoveryCount));
    }

    public Task<LeaseHeartbeat> HeartbeatAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var mutex = EnterMutex(cancellationToken);
        var entry = RequireLease(jobId, workerId, leaseId);
        var now = _timeProvider.GetUtcNow();
        if (entry.State.LeaseExpiresAtUtc <= now)
        {
            throw new LeaseLostException($"The lease for job '{jobId}' has expired.");
        }

        var expires = now + LeaseDuration;
        WriteState(entry.Directory, entry.State with
        {
            UpdatedAtUtc = now,
            HeartbeatAtUtc = now,
            LeaseExpiresAtUtc = expires
        });
        return Task.FromResult(new LeaseHeartbeat(jobId, workerId, leaseId, now, expires));
    }

    public Task CompleteAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        JsonElement body,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var mutex = EnterMutex(cancellationToken);
        var entry = RequireLease(jobId, workerId, leaseId);
        var now = _timeProvider.GetUtcNow();
        EnsureLeaseIsCurrent(entry.State, now);

        var response = new JobResponse(1, jobId, now, body.Clone());
        AtomicWriteJson(Path.Combine(entry.Directory, "response.json"), response);
        WriteState(entry.Directory, ToTerminal(entry.State, LocalJobState.Succeeded, now, null));
        Archive(entry.Directory, jobId);
        return Task.CompletedTask;
    }

    public Task FailAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        cancellationToken.ThrowIfCancellationRequested();
        using var mutex = EnterMutex(cancellationToken);
        var entry = RequireLease(jobId, workerId, leaseId);
        var now = _timeProvider.GetUtcNow();
        EnsureLeaseIsCurrent(entry.State, now);
        WriteState(entry.Directory, ToTerminal(entry.State, LocalJobState.Failed, now, failureCode));
        Archive(entry.Directory, jobId);
        return Task.CompletedTask;
    }

    public Task CancelAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var mutex = EnterMutex(cancellationToken);
        var entry = RequireLease(jobId, workerId, leaseId);
        var now = _timeProvider.GetUtcNow();
        EnsureLeaseIsCurrent(entry.State, now);
        var responsePath = Path.Combine(entry.Directory, "response.json");
        if (File.Exists(responsePath))
        {
            File.Delete(responsePath);
        }

        WriteState(entry.Directory, ToTerminal(entry.State, LocalJobState.Cancelled, now, null));
        Archive(entry.Directory, jobId);
        return Task.CompletedTask;
    }

    public Task<JobResponse?> ReadResponseAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var mutex = EnterMutex(cancellationToken);
        var entry = FindEntry(jobId);
        if (entry is null || entry.State.State != LocalJobState.Succeeded)
        {
            return Task.FromResult<JobResponse?>(null);
        }

        var responsePath = Path.Combine(entry.Directory, "response.json");
        if (!File.Exists(responsePath))
        {
            return Task.FromResult<JobResponse?>(null);
        }

        var response = JsonSerializer.Deserialize<JobResponse>(
            File.ReadAllText(responsePath),
            JsonOptions) ?? throw new InvalidDataException("The durable response is empty.");
        if (response.SchemaVersion != 1 ||
            response.JobId == Guid.Empty ||
            response.JobId != jobId ||
            response.CompletedAtUtc == default)
        {
            throw new InvalidDataException("The durable response is invalid.");
        }

        return Task.FromResult<JobResponse?>(response);
    }

    public Task<JobDiagnostic?> GetDiagnosticAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var mutex = EnterMutex(cancellationToken);
        var entry = FindEntry(jobId);
        if (entry is null)
        {
            return Task.FromResult<JobDiagnostic?>(null);
        }

        var requestPath = Path.Combine(entry.Directory, "request.json");
        var requestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(requestPath)));
        var state = entry.State;
        return Task.FromResult<JobDiagnostic?>(new JobDiagnostic(
            state.JobId,
            state.Sequence,
            requestHash,
            state.State,
            state.CreatedAtUtc,
            state.UpdatedAtUtc,
            state.HeartbeatAtUtc,
            state.LeaseExpiresAtUtc,
            state.AttemptCount,
            state.RecoveryCount,
            state.FailureCode));
    }

    private static void ValidateWorkerId(string workerId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

    private static void EnsureLeaseIsCurrent(JobStateDocument state, DateTimeOffset now)
    {
        if (state.LeaseExpiresAtUtc <= now)
        {
            throw new LeaseLostException($"The lease for job '{state.JobId}' has expired.");
        }
    }

    private static JobStateDocument ToTerminal(
        JobStateDocument state,
        LocalJobState terminalState,
        DateTimeOffset now,
        string? failureCode) =>
        state with
        {
            State = terminalState,
            UpdatedAtUtc = now,
            WorkerId = null,
            LeaseId = null,
            LeaseExpiresAtUtc = null,
            HeartbeatAtUtc = null,
            FailureCode = failureCode
        };

    private JobEntry RequireLease(Guid jobId, string workerId, Guid leaseId)
    {
        ValidateWorkerId(workerId);
        var entry = FindEntry(jobId)
            ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
        if (entry.State.State != LocalJobState.Running)
        {
            throw new LeaseLostException(
                $"Job '{jobId}' is '{entry.State.State}', not '{LocalJobState.Running}'.");
        }

        if (!string.Equals(entry.State.WorkerId, workerId, StringComparison.Ordinal))
        {
            throw new LeaseLostException($"Worker '{workerId}' does not own job '{jobId}'.");
        }

        if (leaseId == Guid.Empty || entry.State.LeaseId != leaseId)
        {
            throw new LeaseLostException($"Lease token does not own job '{jobId}'.");
        }

        return entry;
    }

    private void RecoverExpiredLeases(DateTimeOffset now)
    {
        foreach (var entry in ReadEntries().Where(entry =>
                     entry.State.State == LocalJobState.Running &&
                     entry.State.LeaseExpiresAtUtc <= now))
        {
            WriteState(entry.Directory, entry.State with
            {
                State = LocalJobState.Queued,
                UpdatedAtUtc = now,
                WorkerId = null,
                LeaseId = null,
                LeaseExpiresAtUtc = null,
                HeartbeatAtUtc = null,
                RecoveryCount = entry.State.RecoveryCount + 1,
                FailureCode = null
            });
        }
    }

    private List<JobEntry> ReadEntries()
    {
        var entries = new List<JobEntry>();
        foreach (var directory in Directory.EnumerateDirectories(_jobsRoot))
        {
            var statePath = GetStatePath(directory);
            var requestPath = Path.Combine(directory, "request.json");
            if (!File.Exists(statePath) || !File.Exists(requestPath))
            {
                Quarantine(directory);
                throw new InvalidDataException("An incomplete committed job was quarantined.");
            }

            try
            {
                var state = ReadState(directory);
                var request = ReadRequest(directory);
                ValidateCrossFile(directory, state, request);
                entries.Add(new JobEntry(directory, state));
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or ArgumentException)
            {
                Quarantine(directory);
                throw new InvalidDataException("A corrupt committed job was quarantined.", exception);
            }
        }

        return entries;
    }

    private JobEntry? FindEntry(Guid jobId)
    {
        foreach (var directory in new[] { GetJobDirectory(jobId), GetArchiveDirectory(jobId) })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var state = ReadState(directory);
            var request = ReadRequest(directory);
            ValidateCrossFile(directory, state, request);
            return new JobEntry(directory, state);
        }

        return null;
    }

    private LocalJobRequest ReadRequest(string jobDirectory)
    {
        var document = JsonSerializer.Deserialize<RequestDocument>(
            File.ReadAllText(Path.Combine(jobDirectory, "request.json")),
            JsonOptions)
            ?? throw new InvalidDataException("The durable request is empty.");
        if (document.SchemaVersion != 1 || document.Request is null)
        {
            throw new InvalidDataException("The durable request version is unsupported.");
        }

        if (!Enum.IsDefined(document.Request.Kind) ||
            !Enum.IsDefined(document.Request.Priority))
        {
            throw new InvalidDataException("The durable request enum value is invalid.");
        }

        return document.Request;
    }

    private JobStateDocument ReadState(string jobDirectory)
    {
        var state = JsonSerializer.Deserialize<JobStateDocument>(
            File.ReadAllText(GetStatePath(jobDirectory)),
            JsonOptions) ?? throw new InvalidDataException("The durable state is empty.");
        ValidateState(state);
        return state;
    }

    private long AllocateSequence()
    {
        long current = 0;
        if (File.Exists(_sequencePath))
        {
            var document = JsonSerializer.Deserialize<SequenceDocument>(
                File.ReadAllText(_sequencePath),
                JsonOptions) ?? throw new InvalidDataException("The durable sequence is empty.");
            if (document.SchemaVersion != 1 || document.Value < 0)
            {
                throw new InvalidDataException("The durable sequence is invalid.");
            }

            current = document.Value;
        }

        var next = checked(current + 1);
        AtomicWriteJson(_sequencePath, new SequenceDocument(1, next));
        return next;
    }

    private void WriteState(string jobDirectory, JobStateDocument state) =>
        AtomicWriteJson(GetStatePath(jobDirectory), state);

    private static string GetStatePath(string jobDirectory) =>
        Path.Combine(jobDirectory, "state.json");

    private string GetJobDirectory(Guid jobId) =>
        Path.Combine(_jobsRoot, jobId.ToString("N"));

    private string GetArchiveDirectory(Guid jobId) =>
        Path.Combine(_archiveRoot, jobId.ToString("N"));

    private void MaintainActiveRoot()
    {
        foreach (var staging in Directory.EnumerateDirectories(_stagingRoot, "*.tmp"))
        {
            Directory.Delete(staging, recursive: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(_jobsRoot))
        {
            var statePath = GetStatePath(directory);
            var requestPath = Path.Combine(directory, "request.json");
            if (!File.Exists(statePath) || !File.Exists(requestPath))
            {
                Quarantine(directory);
                throw new InvalidDataException("An incomplete committed job was quarantined.");
            }

            try
            {
                var state = ReadState(directory);
                ValidateCrossFile(directory, state, ReadRequest(directory));
                if (state.State is LocalJobState.Succeeded or LocalJobState.Failed or LocalJobState.Cancelled)
                {
                    Archive(directory, state.JobId);
                }
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or ArgumentException)
            {
                Quarantine(directory);
                throw new InvalidDataException("A corrupt committed job was quarantined.", exception);
            }
        }
    }

    private void Archive(string activeDirectory, Guid jobId)
    {
        var destination = GetArchiveDirectory(jobId);
        if (Directory.Exists(destination))
        {
            throw new InvalidDataException($"Archive already contains job '{jobId}'.");
        }

        Directory.Move(activeDirectory, destination);
    }

    private void Quarantine(string directory)
    {
        var destination = Path.Combine(
            _quarantineRoot,
            Path.GetFileName(directory) + "." + Guid.NewGuid().ToString("N"));
        Directory.Move(directory, destination);
    }

    private static void ValidateCrossFile(
        string directory,
        JobStateDocument state,
        LocalJobRequest request)
    {
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var directoryJobId) ||
            directoryJobId != state.JobId ||
            state.JobId != request.JobId ||
            state.Priority != request.Priority)
        {
            throw new InvalidDataException("Request and state metadata do not match.");
        }
    }

    private static void ValidateState(JobStateDocument state)
    {
        if (state.SchemaVersion != 1 ||
            state.JobId == Guid.Empty ||
            state.Sequence <= 0 ||
            state.CreatedAtUtc == default ||
            state.UpdatedAtUtc == default ||
            state.AttemptCount < 0 ||
            state.RecoveryCount < 0 ||
            state.RecoveryCount > state.AttemptCount ||
            state.UpdatedAtUtc < state.CreatedAtUtc ||
            !Enum.IsDefined(state.Priority) ||
            !Enum.IsDefined(state.State))
        {
            throw new InvalidDataException("The durable state metadata is invalid.");
        }

        var running = state.State == LocalJobState.Running;
        var hasLease = !string.IsNullOrWhiteSpace(state.WorkerId) &&
                       state.LeaseId.HasValue &&
                       state.LeaseId.Value != Guid.Empty &&
                       state.LeaseExpiresAtUtc.HasValue &&
                       state.HeartbeatAtUtc.HasValue;
        if (running != hasLease ||
            (!running && (state.WorkerId is not null ||
                          state.LeaseId is not null ||
                          state.LeaseExpiresAtUtc is not null ||
                          state.HeartbeatAtUtc is not null)))
        {
            throw new InvalidDataException("The durable state lease invariants are invalid.");
        }

        var hasFailure = !string.IsNullOrWhiteSpace(state.FailureCode);
        if ((state.State == LocalJobState.Failed) != hasFailure)
        {
            throw new InvalidDataException("The durable state failure invariants are invalid.");
        }
    }

    private static void AtomicWriteJson<T>(string destination, T value)
    {
        var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private MutexLease EnterMutex(CancellationToken cancellationToken)
    {
        var mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromMilliseconds(100)))
                    {
                        return new MutexLease(mutex);
                    }
                }
                catch (AbandonedMutexException)
                {
                    return new MutexLease(mutex);
                }
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static string CreateMutexName(string runtimeRoot)
    {
        var normalized = OperatingSystem.IsWindows()
            ? runtimeRoot.ToUpperInvariant()
            : runtimeRoot;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return (OperatingSystem.IsWindows() ? @"Global\" : string.Empty) + "LocalAi.Broker." + hash;
    }

    private sealed record SequenceDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] long Value);

    private sealed record RequestDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] LocalJobRequest Request);

    private sealed record JobEntry(string Directory, JobStateDocument State);

    private sealed record JobStateDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid JobId,
        [property: JsonRequired] long Sequence,
        [property: JsonRequired] LocalJobPriority Priority,
        [property: JsonRequired] LocalJobState State,
        [property: JsonRequired] DateTimeOffset CreatedAtUtc,
        [property: JsonRequired] DateTimeOffset UpdatedAtUtc,
        [property: JsonRequired] string? WorkerId,
        [property: JsonRequired] Guid? LeaseId,
        [property: JsonRequired] DateTimeOffset? LeaseExpiresAtUtc,
        [property: JsonRequired] DateTimeOffset? HeartbeatAtUtc,
        [property: JsonRequired] int AttemptCount,
        [property: JsonRequired] int RecoveryCount,
        [property: JsonRequired] string? FailureCode);

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

public sealed record EnqueueResult(Guid JobId, long Sequence, bool JoinedExisting);

public sealed record LeasedJob(
    LocalJobRequest Request,
    long Sequence,
    string WorkerId,
    Guid LeaseId,
    DateTimeOffset LeaseExpiresAtUtc,
    int AttemptCount,
    int RecoveryCount);

public sealed record LeaseHeartbeat(
    Guid JobId,
    string WorkerId,
    Guid LeaseId,
    DateTimeOffset HeartbeatAtUtc,
    DateTimeOffset LeaseExpiresAtUtc);

public sealed record JobResponse(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] Guid JobId,
    [property: JsonRequired] DateTimeOffset CompletedAtUtc,
    [property: JsonRequired] JsonElement Body);

public sealed record JobDiagnostic(
    Guid JobId,
    long Sequence,
    string RequestHash,
    LocalJobState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? HeartbeatAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc,
    int AttemptCount,
    int RecoveryCount,
    string? FailureCode);

public sealed class LeaseLostException(string message) : InvalidOperationException(message);

public interface IBrokerQueue
{
    TimeSpan LeaseDuration { get; }

    Task<LeasedJob?> LeaseNextAsync(
        string workerId,
        CancellationToken cancellationToken = default);

    Task<LeaseHeartbeat> HeartbeatAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        JsonElement body,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        string failureCode,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        CancellationToken cancellationToken = default);
}
