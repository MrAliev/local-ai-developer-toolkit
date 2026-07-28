using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;

#pragma warning disable xUnit1051
#pragma warning disable CA1416

namespace LocalAi.Broker.Tests;

public sealed class BrokerRecoveryTests
{
    [Fact]
    public async Task Heartbeat_extends_lease_and_expired_lease_is_recovered()
    {
        using var root = new TemporaryRuntimeRoot();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero));
        var queue = new DurableQueue(root.Path, time, TimeSpan.FromSeconds(30));
        var enqueue = await queue.EnqueueAsync(Request("recover"));
        var firstLease = await queue.LeaseNextAsync("worker-1");
        Assert.NotNull(firstLease);
        var initialExpiry = firstLease.LeaseExpiresAtUtc;

        time.Advance(TimeSpan.FromSeconds(20));
        var heartbeat = await queue.HeartbeatAsync(enqueue.JobId, "worker-1", firstLease.LeaseId);
        Assert.True(heartbeat.LeaseExpiresAtUtc > initialExpiry);

        time.Advance(TimeSpan.FromSeconds(31));
        var recovered = await new DurableQueue(root.Path, time, TimeSpan.FromSeconds(30))
            .LeaseNextAsync("worker-2");
        Assert.NotNull(recovered);

        Assert.Equal(enqueue.JobId, recovered.Request.JobId);
        Assert.Equal(2, recovered.AttemptCount);
        Assert.Equal(1, recovered.RecoveryCount);
        Assert.Equal("worker-2", recovered.WorkerId);
    }

    [Fact]
    public async Task Recovered_same_worker_is_fenced_from_every_old_lease_operation()
    {
        using var root = new TemporaryRuntimeRoot();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var queue = new DurableQueue(root.Path, time, TimeSpan.FromSeconds(10));
        var enqueue = await queue.EnqueueAsync(Request("fenced"));
        var oldLease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("same-worker"));
        time.Advance(TimeSpan.FromSeconds(11));

        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.CancelAsync(enqueue.JobId, "same-worker", oldLease.LeaseId));
        var newLease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("same-worker"));
        Assert.NotEqual(oldLease.LeaseId, newLease.LeaseId);

        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.HeartbeatAsync(enqueue.JobId, "same-worker", oldLease.LeaseId));
        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.CompleteAsync(enqueue.JobId, "same-worker", oldLease.LeaseId, JsonSerializer.SerializeToElement(true)));
        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.FailAsync(enqueue.JobId, "same-worker", oldLease.LeaseId, "stale"));
        await Assert.ThrowsAsync<LeaseLostException>(
            () => queue.CancelAsync(enqueue.JobId, "same-worker", oldLease.LeaseId));

        await queue.CompleteAsync(
            enqueue.JobId,
            "same-worker",
            newLease.LeaseId,
            JsonSerializer.SerializeToElement(true));
        Assert.Equal(LocalJobState.Succeeded, (await queue.GetDiagnosticAsync(enqueue.JobId))!.State);
    }

    [Fact]
    public async Task BrokerHost_runs_at_most_one_job_and_honors_cancellation()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path, leaseDuration: TimeSpan.FromSeconds(5));
        await queue.EnqueueAsync(Request("one"));
        await queue.EnqueueAsync(Request("two"));
        var concurrent = 0;
        var maximum = 0;
        var completed = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        async Task<BrokerExecutionResult> Execute(LocalJobRequest request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, current);
            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref concurrent);
            if (Interlocked.Increment(ref completed) == 2)
            {
                stop.Cancel();
            }

            return new BrokerExecutionResult(JsonSerializer.SerializeToElement(new { request.JobId }));
        }

        var host = new BrokerHost(
            queue,
            "worker",
            Execute,
            idleDelay: static (delay, token) => Task.Delay(delay, token),
            idleInterval: TimeSpan.FromMilliseconds(5),
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(stop.Token));
        Assert.Equal(1, maximum);
        Assert.Equal(2, completed);
    }

    [Fact]
    public async Task BrokerHost_cancels_active_job_without_publishing_response()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var enqueue = await queue.EnqueueAsync(Request("cancel-host"));
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<BrokerExecutionResult> Execute(LocalJobRequest request, CancellationToken cancellationToken)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }

        var host = new BrokerHost(queue, "worker", Execute);
        var run = host.RunAsync(stop.Token);
        await started.Task;
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(LocalJobState.Cancelled, (await queue.GetDiagnosticAsync(enqueue.JobId))!.State);
        Assert.Null(await queue.ReadResponseAsync(enqueue.JobId));
    }

    [Fact]
    public async Task BrokerHost_synchronous_cooperative_host_cancellation_cancels_not_fails()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var queue = new ScriptedBrokerQueue(TestLease("sync-cancel", "worker"))
        {
            OnLease = stop.Cancel
        };
        var executorSawCancellation = false;

        Task<BrokerExecutionResult> Execute(LocalJobRequest request, CancellationToken token)
        {
            executorSawCancellation = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested();
            throw new UnreachableException();
        }

        var host = new BrokerHost(queue, "worker", Execute);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(stop.Token));
        Assert.True(executorSawCancellation);
        Assert.Equal(1, queue.CancelCalls);
        Assert.Equal(0, queue.FailCalls);
        Assert.Equal(0, queue.CompleteCalls);
    }

    [Fact]
    public async Task BrokerHost_abandons_lost_lease_cancels_attempt_and_continues()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = TestLease("lost", "worker");
        var second = TestLease("recovered", "worker");
        var queue = new ScriptedBrokerQueue(first, second)
        {
            HeartbeatException = new LeaseLostException("stale")
        };
        var cancelledAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        async Task<BrokerExecutionResult> Execute(LocalJobRequest request, CancellationToken token)
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelledAttempt.SetResult();
                    throw;
                }
            }

            stop.Cancel();
            return new BrokerExecutionResult(JsonSerializer.SerializeToElement(true));
        }

        var host = new BrokerHost(
            queue,
            "worker",
            Execute,
            idleDelay: static (delay, token) => Task.Delay(delay, token),
            heartbeatInterval: TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(stop.Token));
        await cancelledAttempt.Task;
        Assert.Equal(2, executions);
        Assert.Equal(0, queue.FailCalls);
        Assert.Equal(1, queue.CompleteCalls);
    }

    [Fact]
    public async Task BrokerHost_unexpected_heartbeat_fault_cancels_attempt_reports_and_fails_fast()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lease = TestLease("heartbeat-io", "worker");
        var queue = new ScriptedBrokerQueue(lease)
        {
            HeartbeatException = new IOException("heartbeat storage")
        };
        var diagnostics = new List<BrokerHostDiagnostic>();
        var executorCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<BrokerExecutionResult> Execute(LocalJobRequest request, CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                executorCancelled.SetResult();
                throw;
            }

            throw new UnreachableException();
        }

        var host = new BrokerHost(
            queue,
            "worker",
            Execute,
            idleDelay: static (delay, token) => Task.Delay(delay, token),
            heartbeatInterval: TimeSpan.FromMilliseconds(1),
            diagnostic: diagnostics.Add);

        var exception = await Assert.ThrowsAsync<IOException>(() => host.RunAsync(stop.Token));
        Assert.Equal("heartbeat storage", exception.Message);
        await executorCancelled.Task;
        Assert.Equal(0, queue.FailCalls);
        Assert.Equal(0, queue.CompleteCalls);
        Assert.Equal(0, queue.CancelCalls);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(lease.Request.JobId, diagnostic.JobId);
        Assert.Equal("worker", diagnostic.WorkerId);
        Assert.Equal(lease.LeaseId, diagnostic.LeaseId);
        Assert.Equal("heartbeat", diagnostic.Operation);
        Assert.Equal(nameof(IOException), diagnostic.ExceptionType);
        Assert.DoesNotContain("heartbeat storage", JsonSerializer.Serialize(diagnostic));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrokerHost_executor_failure_calls_fail_once_and_continues(bool synchronous)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var queue = new ScriptedBrokerQueue(
            TestLease("failure", "worker"),
            TestLease("next", "worker"));
        var executions = 0;

        Task<BrokerExecutionResult> Execute(LocalJobRequest request, CancellationToken token)
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                if (synchronous)
                {
                    throw new InvalidOperationException("sync");
                }

                return Task.FromException<BrokerExecutionResult>(
                    new InvalidOperationException("async"));
            }

            stop.Cancel();
            return Task.FromResult(new BrokerExecutionResult(JsonSerializer.SerializeToElement(true)));
        }

        var host = new BrokerHost(queue, "worker", Execute);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(stop.Token));

        Assert.Equal(2, executions);
        Assert.Equal(1, queue.FailCalls);
        Assert.Equal(1, queue.CompleteCalls);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    public async Task BrokerHost_terminal_write_failure_reports_bounded_diagnostic_and_continues(
        string operation)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var queue = new ScriptedBrokerQueue(
            TestLease("first-write", "worker"),
            TestLease("second-write", "worker"))
        {
            CompleteException = operation == "complete" ? new IOException("disk") : null,
            FailException = operation == "fail" ? new IOException("disk") : null
        };
        var diagnostics = new List<BrokerHostDiagnostic>();
        var executions = 0;

        Task<BrokerExecutionResult> Execute(LocalJobRequest request, CancellationToken token)
        {
            var count = Interlocked.Increment(ref executions);
            if (count == 1 && operation == "fail")
            {
                throw new InvalidOperationException("executor");
            }

            if (count == 2)
            {
                stop.Cancel();
            }

            return Task.FromResult(new BrokerExecutionResult(JsonSerializer.SerializeToElement(
                new { secret = "must-not-appear" })));
        }

        var host = new BrokerHost(
            queue,
            "worker",
            Execute,
            diagnostic: diagnostics.Add);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(stop.Token));

        Assert.Equal(2, executions);
        Assert.Single(diagnostics);
        Assert.Equal(operation, diagnostics[0].Operation);
        Assert.Equal(nameof(IOException), diagnostics[0].ExceptionType);
        var diagnosticJson = JsonSerializer.Serialize(diagnostics[0]);
        Assert.DoesNotContain("secret", diagnosticJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Inputs", diagnosticJson, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeAcl_builds_bounded_current_user_and_administrators_intent()
    {
        using var root = new TemporaryRuntimeRoot();
        var applications = new List<(string Path, bool IsDirectory, bool CreateNew, string User, string Admin)>();
        var acl = new RuntimeAcl(
            isWindows: true,
            currentUser: "DOMAIN\\user",
            applyExactAcl: (path, isDirectory, createNew, user, admin) =>
                applications.Add((path, isDirectory, createNew, user, admin)),
            readAclSnapshot: path => new RuntimeAclSnapshot(
                IsDirectory: true,
                AreAccessRulesProtected: true,
                [
                    new RuntimeAclEntry(
                        "DOMAIN\\user",
                        FileSystemRights.FullControl,
                        AccessControlType.Allow,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        IsInherited: false),
                    new RuntimeAclEntry(
                        "S-1-5-32-544",
                        FileSystemRights.FullControl,
                        AccessControlType.Allow,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        IsInherited: false)
                ]),
            normalizeTrustee: trustee => trustee.TrimStart('*'));

        acl.Ensure(root.Path);

        Assert.NotEmpty(applications);
        Assert.All(applications, application =>
        {
            Assert.Equal("DOMAIN\\user", application.User);
            Assert.Equal("S-1-5-32-544", application.Admin);
            Assert.True(application.IsDirectory);
        });
    }

    [Fact]
    public void RuntimeAcl_non_windows_path_never_invokes_windows_principal_or_acl_delegates()
    {
        using var parent = new TemporaryRuntimeRoot();
        var root = System.IO.Path.Combine(parent.Path, "portable-runtime");
        var acl = new RuntimeAcl(
            isWindows: false,
            currentUser: "must-not-resolve",
            applyExactAcl: (_, _, _, _, _) => throw new UnreachableException(),
            readAclSnapshot: _ => throw new UnreachableException(),
            normalizeTrustee: _ => throw new UnreachableException());

        acl.Ensure(root);

        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(System.IO.Path.Combine(root, "jobs")));
    }

    [Fact]
    public void RuntimeAcl_applies_to_a_temporary_directory_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryRuntimeRoot();
        new RuntimeAcl().Ensure(root.Path);
    }

    [Fact]
    public void RuntimeAcl_creates_new_root_with_exact_acl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var parent = new TemporaryRuntimeRoot();
        var newRoot = System.IO.Path.Combine(parent.Path, "new-runtime");

        new RuntimeAcl().Ensure(newRoot);

        Assert.True(Directory.Exists(newRoot));
        var security = new DirectoryInfo(newRoot).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            2,
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count);
    }

    [Fact]
    public void RuntimeAcl_setter_failure_preserves_previous_restrictive_dacl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryRuntimeRoot();
        var acl = new RuntimeAcl();
        acl.Ensure(root.Path);
        var before = new DirectoryInfo(root.Path)
            .GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var failing = new RuntimeAcl(
            applyExactAcl: (_, _, _, _, _) => throw new IOException("setter"));

        Assert.Throws<IOException>(() => failing.Ensure(root.Path));

        var after = new DirectoryInfo(root.Path)
            .GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.Equal(before, after);
    }

    [Fact]
    public void RuntimeAcl_replaces_unrelated_access_trustees_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryRuntimeRoot();
        var descendantDirectory = System.IO.Path.Combine(root.Path, "existing");
        Directory.CreateDirectory(descendantDirectory);
        File.WriteAllText(System.IO.Path.Combine(descendantDirectory, "existing.txt"), "test");
        var directory = new DirectoryInfo(root.Path);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        new RuntimeAcl().Ensure(root.Path);

        var resultingSecurity = directory.GetAccessControl();
        var actualTrustees = resultingSecurity
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => rule.IdentityReference.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedTrustees = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            WindowsIdentity.GetCurrent().User!.Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
        };

        Assert.Equal(expectedTrustees.Order(), actualTrustees.Order());
    }

    [Fact]
    public void RuntimeAcl_rejects_extra_everyone_allow()
    {
        AssertAclSemanticRejection((path, user, administrators) =>
        {
            SetExactAcl(
                path,
                protectedAcl: true,
                FullControlRule(user, AccessControlType.Allow),
                FullControlRule(administrators, AccessControlType.Allow),
                FullControlRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    AccessControlType.Allow));
        });
    }

    [Fact]
    public void RuntimeAcl_rejects_reduced_current_user_rights()
    {
        AssertAclSemanticRejection((path, user, administrators) =>
        {
            SetExactAcl(
                path,
                protectedAcl: true,
                new FileSystemAccessRule(
                    user,
                    FileSystemRights.Read,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow),
                FullControlRule(administrators, AccessControlType.Allow));
        });
    }

    [Fact]
    public void RuntimeAcl_rejects_any_deny_rule()
    {
        AssertAclSemanticRejection((path, user, administrators) =>
        {
            SetExactAcl(
                path,
                protectedAcl: true,
                FullControlRule(user, AccessControlType.Allow),
                FullControlRule(administrators, AccessControlType.Allow),
                new FileSystemAccessRule(
                    user,
                    FileSystemRights.Delete,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Deny));
        });
    }

    [Fact]
    public void RuntimeAcl_rejects_unprotected_or_inherited_dacl()
    {
        AssertAclSemanticRejection((path, user, administrators) =>
        {
            SetExactAcl(
                path,
                protectedAcl: false,
                FullControlRule(user, AccessControlType.Allow),
                FullControlRule(administrators, AccessControlType.Allow));
        });
    }

    [Fact]
    public void RuntimeAcl_rejects_extra_trustee_on_descendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryRuntimeRoot();
        var acl = new RuntimeAcl();
        try
        {
            acl.Ensure(root.Path);
            var child = System.IO.Path.Combine(root.Path, "jobs", "child");
            Directory.CreateDirectory(child);
            var security = new DirectoryInfo(child).GetAccessControl();
            security.AddAccessRule(FullControlRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                AccessControlType.Allow));
            new DirectoryInfo(child).SetAccessControl(security);

            Assert.Throws<InvalidOperationException>(() => NoOpRuntimeAcl().Ensure(root.Path));
        }
        finally
        {
            acl.Ensure(root.Path);
        }
    }

    private static void AssertAclSemanticRejection(
        Action<string, SecurityIdentifier, SecurityIdentifier> mutate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryRuntimeRoot();
        var acl = new RuntimeAcl();
        try
        {
            acl.Ensure(root.Path);
            mutate(
                root.Path,
                WindowsIdentity.GetCurrent().User!,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

            Assert.Throws<InvalidOperationException>(() => NoOpRuntimeAcl().Ensure(root.Path));
        }
        finally
        {
            acl.Ensure(root.Path);
        }
    }

    private static RuntimeAcl NoOpRuntimeAcl() =>
        new(applyExactAcl: (_, _, _, _, _) => { });

    private static FileSystemAccessRule FullControlRule(
        IdentityReference identity,
        AccessControlType type) =>
        new(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            type);

    private static void SetExactAcl(
        string path,
        bool protectedAcl,
        params FileSystemAccessRule[] rules)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(protectedAcl, preserveInheritance: false);
        foreach (var rule in rules)
        {
            security.AddAccessRule(rule);
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateEmbed(
            key,
            LocalJobPriority.Foreground,
            "test-model",
            ["input"]);

    private static LeasedJob TestLease(string key, string worker)
    {
        var request = Request(key);
        return new LeasedJob(
            request,
            Sequence: 1,
            worker,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            AttemptCount: 1,
            RecoveryCount: 0);
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}

internal sealed class ScriptedBrokerQueue(params LeasedJob[] leases) : IBrokerQueue
{
    private readonly Queue<LeasedJob> _leases = new(leases);

    public Exception? HeartbeatException { get; set; }

    public Exception? CompleteException { get; set; }

    public Exception? FailException { get; set; }

    public int CompleteCalls { get; private set; }

    public int FailCalls { get; private set; }

    public int CancelCalls { get; private set; }

    public Action? OnLease { get; set; }

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(30);

    public Task<LeasedJob?> LeaseNextAsync(string workerId, CancellationToken cancellationToken = default)
    {
        var lease = _leases.Count == 0 ? null : _leases.Dequeue();
        if (lease is not null)
        {
            OnLease?.Invoke();
        }

        return Task.FromResult(lease);
    }

    public Task<LeaseHeartbeat> HeartbeatAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        if (HeartbeatException is { } exception)
        {
            HeartbeatException = null;
            throw exception;
        }

        return Task.FromResult(new LeaseHeartbeat(
            jobId,
            workerId,
            leaseId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    public Task CompleteAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        JsonElement body,
        CancellationToken cancellationToken = default)
    {
        CompleteCalls++;
        if (CompleteException is { } exception)
        {
            CompleteException = null;
            throw exception;
        }

        return Task.CompletedTask;
    }

    public Task FailAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        FailCalls++;
        if (FailException is { } exception)
        {
            FailException = null;
            throw exception;
        }

        return Task.CompletedTask;
    }

    public Task CancelAsync(
        Guid jobId,
        string workerId,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        CancelCalls++;
        return Task.CompletedTask;
    }
}
