using System.Diagnostics;
using LocalAi.Contracts.Activation;

namespace LocalAi.Launcher.Tests;

public sealed class VersionLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-version-lease-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Multiple_shared_leases_block_exclusive_activation()
    {
        var path = Path.Combine(_root, "current.lock");
        using var first = VersionLease.AcquireShared(path);
        using var second = VersionLease.AcquireShared(path);

        var error = Assert.Throws<LauncherException>(
            () => VersionLease.AcquireExclusive(path, TimeSpan.Zero));

        Assert.Equal("version_in_use", error.Code);
    }

    [Fact]
    public void Exclusive_activation_succeeds_after_shared_lease_is_released()
    {
        var path = Path.Combine(_root, "current.lock");
        using (VersionLease.AcquireShared(path))
        {
        }

        using var exclusive = VersionLease.AcquireExclusive(
            path,
            TimeSpan.Zero);

        Assert.NotNull(exclusive);
    }

    [Fact]
    public void Hostile_named_mutex_precreate_is_rejected_with_sanitized_code()
    {
        var factory = new FakeSecureNamedMutexFactory(
            _ => throw new UnauthorizedAccessException("hostile descriptor details"));

        var error = Assert.Throws<ActivationCoordinationException>(() =>
            ActivationCoordinator.AcquireStartupGate(
                _root,
                TimeSpan.Zero,
                factory,
                TestContext.Current.CancellationToken));

        Assert.Equal("activation_unavailable", error.Code);
        Assert.DoesNotContain("hostile", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secure_gate_name_is_local_and_bound_to_current_user()
    {
        var held = new FakeSecureNamedMutex(waitResult: true);
        var factory = new FakeSecureNamedMutexFactory(_ => held);

        using var gate = ActivationCoordinator.AcquireStartupGate(
            _root,
            TimeSpan.Zero,
            factory,
            TestContext.Current.CancellationToken);

        Assert.StartsWith(@"Local\LocalAi.Launcher.Activation.", factory.Name, StringComparison.Ordinal);
        Assert.Equal(1, held.WaitCount);
        Assert.Equal(0, held.ReleaseCount);
        gate.Dispose();
        Assert.Equal(1, held.ReleaseCount);
    }

    [Fact]
    public async Task Exclusive_uses_one_total_timeout_for_gate_and_file_lease()
    {
        var path = Path.Combine(_root, "current.lock");
        Directory.CreateDirectory(_root);
        using var shared = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            using var gate = ActivationCoordinator.AcquireStartupGate(_root, TimeSpan.FromSeconds(1));
            gateHeld.SetResult();
            Thread.Sleep(700);
        }, TestContext.Current.CancellationToken);
        await gateHeld.Task;
        var stopwatch = Stopwatch.StartNew();

        var error = Assert.Throws<LauncherException>(() =>
            VersionLease.AcquireExclusive(path, TimeSpan.FromSeconds(1)));

        stopwatch.Stop();
        await holder;
        Assert.Equal("version_in_use", error.Code);

        // The regression guarded against is spending the budget twice — once on the startup gate
        // and again on the file lease — so the only thing the bounds must do is separate one
        // budget from two. That separation has to be wider than the machine's scheduling
        // jitter, and twice it was not: the bound was 280 ms, was raised to 360 ms for the same
        // reason, and a CI runner then took 731 ms of correct behaviour. Raising it again would
        // have been the third time measuring the machine rather than the code.
        //
        // The gap between the two outcomes is exactly how long the gate is held, so the hold has
        // to be a large share of the budget or there is nothing to measure. Holding for 700 ms
        // of a one-second budget puts a correct run at about a second and a double-spending one
        // at about 1.7 — 700 ms apart, where several hundred milliseconds of jitter no longer
        // reach the boundary. With the old 120 ms hold the two were 1.0 and 1.12 seconds apart
        // and the mutation survived, which is what a bound tuned for the machine rather than the
        // behaviour buys.
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(850),
            TimeSpan.FromMilliseconds(1_400));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSecureNamedMutexFactory(
        Func<string, ISecureNamedMutex> create) : ISecureNamedMutexFactory
    {
        public string? Name { get; private set; }

        public ISecureNamedMutex Create(string name)
        {
            Name = name;
            return create(name);
        }
    }

    private sealed class FakeSecureNamedMutex(bool waitResult) : ISecureNamedMutex
    {
        public int WaitCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public bool WaitOne(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WaitCount++;
            return waitResult;
        }

        public void Release() => ReleaseCount++;
        public void Dispose() { }
    }
}
