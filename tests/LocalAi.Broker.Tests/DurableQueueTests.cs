using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Broker;
using LocalAi.Contracts;

#pragma warning disable xUnit1051

namespace LocalAi.Broker.Tests;

public sealed class DurableQueueTests
{
    [Fact]
    public async Task Candidate_listing_and_chosen_leasing_are_atomic_and_preserve_single_runner()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var first = Request("first", LocalJobPriority.Foreground);
        var second = Request("second", LocalJobPriority.Background);
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);

        var candidates = await queue.ListQueuedAsync();
        Assert.Equal(
            [first.JobId, second.JobId],
            candidates.Select(value => value.Request.JobId));

        var lease = Assert.IsType<LeasedJob>(
            await queue.TryLeaseAsync(second.JobId, "scheduler"));
        Assert.Equal(second.JobId, lease.Request.JobId);
        Assert.Null(await queue.TryLeaseAsync(first.JobId, "scheduler"));
    }

    [Fact]
    public async Task Persisted_request_round_trip_preserves_concrete_payload_and_immutable_collection()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var images = new List<string> { "image" };
        var request = LocalJobRequestFactory.CreateChat(
            "typed-payload",
            LocalJobPriority.Foreground,
            "chat-model",
            "prompt",
            null,
            images);

        await queue.EnqueueAsync(request);
        images[0] = "changed";
        var lease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker"));
        var payload = Assert.IsType<ChatJobPayload>(lease.Request.Payload);

        Assert.Equal(LocalJobKind.Chat, lease.Request.Kind);
        Assert.Equal("image", Assert.Single(payload.ImagesBase64));
        var collectionView = Assert.IsAssignableFrom<IList<string>>(payload.ImagesBase64);
        Assert.Throws<NotSupportedException>(() => collectionView[0] = "changed");
    }

    [Fact]
    public async Task LeaseNext_orders_by_priority_then_fifo()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var background1 = Request("background-1", LocalJobPriority.Background);
        var interactive1 = Request("interactive-1", LocalJobPriority.Interactive);
        var foreground1 = Request("foreground-1", LocalJobPriority.Foreground);
        var interactive2 = Request("interactive-2", LocalJobPriority.Interactive);

        await queue.EnqueueAsync(background1);
        await queue.EnqueueAsync(interactive1);
        await queue.EnqueueAsync(foreground1);
        await queue.EnqueueAsync(interactive2);

        var order = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            var lease = await queue.LeaseNextAsync("worker");
            Assert.NotNull(lease);
            order.Add(lease.Request.JobId);
            await queue.FailAsync(lease.Request.JobId, "worker", lease.LeaseId, "expected-test-failure");
        }

        Assert.Equal(
            [interactive1.JobId, interactive2.JobId, foreground1.JobId, background1.JobId],
            order);
    }

    [Fact]
    public async Task Concurrent_enqueues_allocate_unique_monotonic_sequences()
    {
        using var root = new TemporaryRuntimeRoot();
        var queues = Enumerable.Range(0, 4).Select(_ => new DurableQueue(root.Path)).ToArray();

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            queues[index % queues.Length].EnqueueAsync(Request($"key-{index}"))));

        Assert.Equal(40, results.Select(result => result.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 40).Select(value => (long)value), results.Select(result => result.Sequence).Order());
    }

    [Fact]
    public async Task Equivalent_runtime_root_spellings_share_sequence_and_active_lease()
    {
        using var root = new TemporaryRuntimeRoot();
        var withSeparator = root.Path + System.IO.Path.DirectorySeparatorChar;
        var firstQueue = new DurableQueue(root.Path);
        var secondQueue = new DurableQueue(withSeparator);
        var enqueueGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.Equal(firstQueue.RuntimeRoot, secondQueue.RuntimeRoot);
        var enqueueTasks = new[]
        {
            Task.Run(async () =>
            {
                await enqueueGate.Task;
                return await firstQueue.EnqueueAsync(Request("equivalent-1"));
            }),
            Task.Run(async () =>
            {
                await enqueueGate.Task;
                return await secondQueue.EnqueueAsync(Request("equivalent-2"));
            })
        };
        enqueueGate.SetResult();
        var enqueues = await Task.WhenAll(enqueueTasks);

        Assert.Equal([1L, 2L], enqueues.Select(result => result.Sequence).Order());
        var leaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseTasks = new[]
        {
            Task.Run(async () =>
            {
                await leaseGate.Task;
                return await firstQueue.LeaseNextAsync("worker-1");
            }),
            Task.Run(async () =>
            {
                await leaseGate.Task;
                return await secondQueue.LeaseNextAsync("worker-2");
            })
        };
        leaseGate.SetResult();
        var leases = await Task.WhenAll(leaseTasks);

        Assert.Single(leases, lease => lease is not null);
    }

    [Fact]
    public async Task Active_deduplication_joins_but_terminal_job_allows_resubmission()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var first = await queue.EnqueueAsync(Request("stable-key"));
        var queuedJoin = await queue.EnqueueAsync(Request("stable-key"));

        Assert.True(queuedJoin.JoinedExisting);
        Assert.Equal(first.JobId, queuedJoin.JobId);

        var lease = await queue.LeaseNextAsync("worker");
        Assert.NotNull(lease);
        var runningJoin = await queue.EnqueueAsync(Request("stable-key"));
        Assert.True(runningJoin.JoinedExisting);
        Assert.Equal(first.JobId, runningJoin.JobId);

        await queue.FailAsync(lease.Request.JobId, "worker", lease.LeaseId, "terminal");
        var resubmission = await queue.EnqueueAsync(Request("stable-key"));
        Assert.False(resubmission.JoinedExisting);
        Assert.NotEqual(first.JobId, resubmission.JobId);
        Assert.True(resubmission.Sequence > first.Sequence);
    }

    [Fact]
    public async Task Only_one_lease_is_active_and_wrong_worker_is_rejected()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        await queue.EnqueueAsync(Request("first"));
        await queue.EnqueueAsync(Request("second"));

        var lease = await queue.LeaseNextAsync("worker-1");
        Assert.NotNull(lease);
        Assert.Null(await new DurableQueue(root.Path).LeaseNextAsync("worker-2"));
        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.HeartbeatAsync(lease.Request.JobId, "worker-2", lease.LeaseId));
        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.CompleteAsync(lease.Request.JobId, "worker-2", lease.LeaseId, JsonSerializer.SerializeToElement(new { ok = true })));
        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.FailAsync(lease.Request.JobId, "worker-2", lease.LeaseId, "failure"));
        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.CancelAsync(lease.Request.JobId, "worker-2", lease.LeaseId));
    }

    [Fact]
    public async Task Completion_failure_and_cancellation_are_atomic_terminal_transitions()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);

        var completed = await queue.EnqueueAsync(Request("completed"));
        var completedLease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker"));
        await queue.CompleteAsync(completed.JobId, "worker", completedLease.LeaseId, JsonSerializer.SerializeToElement(new { answer = 42 }));
        var response = await queue.ReadResponseAsync(completed.JobId);
        Assert.NotNull(response);
        Assert.Equal(42, response.Body.GetProperty("answer").GetInt32());
        Assert.Equal(LocalJobState.Succeeded, (await queue.GetDiagnosticAsync(completed.JobId))!.State);
        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.FailAsync(completed.JobId, "worker", completedLease.LeaseId, "late"));

        var failed = await queue.EnqueueAsync(Request("failed"));
        var failedLease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker"));
        await queue.FailAsync(failed.JobId, "worker", failedLease.LeaseId, "failure-code");
        Assert.Null(await queue.ReadResponseAsync(failed.JobId));
        Assert.Equal(LocalJobState.Failed, (await queue.GetDiagnosticAsync(failed.JobId))!.State);

        var cancelled = await queue.EnqueueAsync(Request("cancelled"));
        var cancelledLease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker"));
        await queue.CancelAsync(cancelled.JobId, "worker", cancelledLease.LeaseId);
        Assert.Null(await queue.ReadResponseAsync(cancelled.JobId));
        Assert.Equal(LocalJobState.Cancelled, (await queue.GetDiagnosticAsync(cancelled.JobId))!.State);
        Assert.False(File.Exists(System.IO.Path.Combine(root.Path, "jobs", cancelled.JobId.ToString("N"), "response.json")));
    }

    [Fact]
    public async Task Partial_temp_files_are_ignored_and_diagnostics_do_not_expose_inputs()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var request = Request("safe-key", inputs: ["secret prompt body"]);
        await queue.EnqueueAsync(request);
        var jobDirectory = System.IO.Path.Combine(root.Path, "jobs", request.JobId.ToString("N"));
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(jobDirectory, "response.json.tmp"),
            """{"Body":{"secret":"partial"}}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(root.Path, "jobs", "orphan.tmp"),
            "partial",
            TestContext.Current.CancellationToken);

        Assert.Null(await queue.ReadResponseAsync(request.JobId));
        var diagnostic = await queue.GetDiagnosticAsync(request.JobId);
        Assert.NotNull(diagnostic);
        var json = JsonSerializer.Serialize(diagnostic);
        Assert.DoesNotContain("secret prompt body", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Inputs", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staging_remnant_is_cleaned_before_atomic_enqueue_commit()
    {
        using var root = new TemporaryRuntimeRoot();
        var staging = System.IO.Path.Combine(root.Path, "staging", "abandoned.tmp");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(staging, "request.json"),
            "partial",
            TestContext.Current.CancellationToken);

        var result = await new DurableQueue(root.Path).EnqueueAsync(Request("after-staging"));

        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(System.IO.Path.Combine(
            root.Path,
            "jobs",
            result.JobId.ToString("N"))));
    }

    [Theory]
    [InlineData("request.json", "{}")]
    [InlineData("state.json", "{}")]
    [InlineData("request.json", "{corrupt")]
    public async Task Incomplete_or_corrupt_final_is_quarantined_and_retry_can_commit(
        string fileName,
        string contents)
    {
        using var root = new TemporaryRuntimeRoot();
        var request = Request("retry-after-corrupt");
        var queue = new DurableQueue(root.Path);
        var finalDirectory = System.IO.Path.Combine(root.Path, "jobs", request.JobId.ToString("N"));
        Directory.CreateDirectory(finalDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(finalDirectory, fileName),
            contents,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => queue.EnqueueAsync(request));
        Assert.False(Directory.Exists(finalDirectory));
        Assert.NotEmpty(Directory.EnumerateDirectories(System.IO.Path.Combine(root.Path, "quarantine")));

        var retry = await queue.EnqueueAsync(request);
        Assert.Equal(request.JobId, retry.JobId);
        Assert.False(retry.JoinedExisting);
    }

    [Fact]
    public async Task Fully_present_but_corrupt_final_is_quarantined()
    {
        using var root = new TemporaryRuntimeRoot();
        var request = Request("corrupt-final");
        var queue = new DurableQueue(root.Path);
        var finalDirectory = System.IO.Path.Combine(root.Path, "jobs", request.JobId.ToString("N"));
        Directory.CreateDirectory(finalDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(finalDirectory, "request.json"),
            "{corrupt",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(finalDirectory, "state.json"),
            "{corrupt",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => queue.EnqueueAsync(request));
        Assert.False(Directory.Exists(finalDirectory));
        Assert.Equal(request.JobId, (await queue.EnqueueAsync(request)).JobId);
    }

    [Fact]
    public async Task Payload_validation_failure_is_quarantined_and_next_operation_can_continue()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var poisoned = Request("poison-payload");
        await queue.EnqueueAsync(poisoned);
        var directory = System.IO.Path.Combine(
            root.Path,
            "jobs",
            poisoned.JobId.ToString("N"));
        var requestPath = System.IO.Path.Combine(directory, "request.json");
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(
            requestPath,
            TestContext.Current.CancellationToken))!.AsObject();
        envelope["Request"]!["Payload"]!["Model"] = " ";
        await File.WriteAllTextAsync(
            requestPath,
            envelope.ToJsonString(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => queue.EnqueueAsync(Request("after-poison")));

        Assert.False(Directory.Exists(directory));
        Assert.NotEmpty(Directory.EnumerateDirectories(
            System.IO.Path.Combine(root.Path, "quarantine")));
        var retry = await queue.EnqueueAsync(Request("after-poison"));
        Assert.False(retry.JoinedExisting);
    }

    [Fact]
    public async Task Unsupported_sequence_schema_fails_closed()
    {
        using var root = new TemporaryRuntimeRoot();
        _ = new DurableQueue(root.Path);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(root.Path, "sequence.json"),
            """{"SchemaVersion":2,"Value":1}""",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new DurableQueue(root.Path).EnqueueAsync(Request("unsupported-sequence")));
    }

    [Fact]
    public async Task Cross_file_job_identity_mismatch_is_quarantined_and_never_leased()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var enqueue = await queue.EnqueueAsync(Request("mismatch"));
        var statePath = System.IO.Path.Combine(
            root.Path,
            "jobs",
            enqueue.JobId.ToString("N"),
            "state.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(
            statePath,
            TestContext.Current.CancellationToken))!.AsObject();
        state["JobId"] = Guid.NewGuid();
        await File.WriteAllTextAsync(
            statePath,
            state.ToJsonString(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => queue.LeaseNextAsync("worker"));
        Assert.Empty(Directory.EnumerateDirectories(System.IO.Path.Combine(root.Path, "jobs")));
    }

    [Fact]
    public async Task Coherently_changed_metadata_that_disagrees_with_directory_name_is_quarantined()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var enqueue = await queue.EnqueueAsync(Request("directory-identity"));
        var directory = System.IO.Path.Combine(root.Path, "jobs", enqueue.JobId.ToString("N"));
        var changedId = Guid.NewGuid();

        var requestPath = System.IO.Path.Combine(directory, "request.json");
        var requestEnvelope = JsonNode.Parse(await File.ReadAllTextAsync(
            requestPath,
            TestContext.Current.CancellationToken))!.AsObject();
        requestEnvelope["Request"]!["JobId"] = changedId;
        await File.WriteAllTextAsync(
            requestPath,
            requestEnvelope.ToJsonString(),
            TestContext.Current.CancellationToken);

        var statePath = System.IO.Path.Combine(directory, "state.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(
            statePath,
            TestContext.Current.CancellationToken))!.AsObject();
        state["JobId"] = changedId;
        await File.WriteAllTextAsync(
            statePath,
            state.ToJsonString(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => queue.LeaseNextAsync("worker"));
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("version")]
    public async Task Missing_unknown_or_cross_version_state_fails_closed(string corruption)
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var enqueue = await queue.EnqueueAsync(Request($"state-{corruption}"));
        var statePath = System.IO.Path.Combine(
            root.Path,
            "jobs",
            enqueue.JobId.ToString("N"),
            "state.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(
            statePath,
            TestContext.Current.CancellationToken))!.AsObject();
        if (corruption == "missing")
        {
            state.Remove("Sequence");
        }
        else if (corruption == "unknown")
        {
            state["Unexpected"] = true;
        }
        else
        {
            state["SchemaVersion"] = 2;
        }

        await File.WriteAllTextAsync(
            statePath,
            state.ToJsonString(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => queue.LeaseNextAsync("worker"));
    }

    [Fact]
    public async Task Unsupported_request_envelope_fails_closed()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var enqueue = await queue.EnqueueAsync(Request("request-version"));
        var requestPath = System.IO.Path.Combine(
            root.Path,
            "jobs",
            enqueue.JobId.ToString("N"),
            "request.json");
        var request = JsonNode.Parse(await File.ReadAllTextAsync(
            requestPath,
            TestContext.Current.CancellationToken))!.AsObject();
        request["SchemaVersion"] = 2;
        await File.WriteAllTextAsync(
            requestPath,
            request.ToJsonString(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => queue.LeaseNextAsync("worker"));
    }

    [Fact]
    public async Task Terminal_response_job_mismatch_fails_closed()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var enqueue = await queue.EnqueueAsync(Request("response-mismatch"));
        var lease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker"));
        await queue.CompleteAsync(
            enqueue.JobId,
            "worker",
            lease.LeaseId,
            JsonSerializer.SerializeToElement(true));
        var responsePath = System.IO.Path.Combine(
            root.Path,
            "archive",
            enqueue.JobId.ToString("N"),
            "response.json");
        var response = JsonNode.Parse(await File.ReadAllTextAsync(
            responsePath,
            TestContext.Current.CancellationToken))!.AsObject();
        response["JobId"] = Guid.NewGuid();
        await File.WriteAllTextAsync(
            responsePath,
            response.ToJsonString(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => queue.ReadResponseAsync(enqueue.JobId));
    }

    [Fact]
    public async Task Terminal_jobs_are_archived_and_active_operations_ignore_corrupt_history()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        for (var index = 0; index < 150; index++)
        {
            var corruptArchive = System.IO.Path.Combine(root.Path, "archive", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(corruptArchive);
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(corruptArchive, "state.json"),
                "{corrupt",
                TestContext.Current.CancellationToken);
        }

        var enqueue = await queue.EnqueueAsync(Request("active-with-history"));
        var lease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker"));
        await queue.HeartbeatAsync(enqueue.JobId, "worker", lease.LeaseId);
        await queue.CompleteAsync(
            enqueue.JobId,
            "worker",
            lease.LeaseId,
            JsonSerializer.SerializeToElement(true));

        Assert.False(Directory.Exists(System.IO.Path.Combine(root.Path, "jobs", enqueue.JobId.ToString("N"))));
        Assert.True(Directory.Exists(System.IO.Path.Combine(root.Path, "archive", enqueue.JobId.ToString("N"))));
        Assert.NotNull(await queue.ReadResponseAsync(enqueue.JobId));
    }

    private static LocalJobRequest Request(
        string key,
        LocalJobPriority priority = LocalJobPriority.Foreground,
        IReadOnlyList<string>? inputs = null) =>
        LocalJobRequestFactory.CreateEmbed(
            key,
            priority,
            "test-model",
            inputs ?? ["input"]);
}

internal sealed class TemporaryRuntimeRoot : IDisposable
{
    public TemporaryRuntimeRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "LocalAi.Broker.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
