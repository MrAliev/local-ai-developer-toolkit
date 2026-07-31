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
                factory));

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
            factory);

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
            Thread.Sleep(120);
        }, TestContext.Current.CancellationToken);
        await gateHeld.Task;
        var stopwatch = Stopwatch.StartNew();

        var error = Assert.Throws<LauncherException>(() =>
            VersionLease.AcquireExclusive(path, TimeSpan.FromMilliseconds(200)));

        stopwatch.Stop();
        await holder;
        Assert.Equal("version_in_use", error.Code);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(170), TimeSpan.FromMilliseconds(280));
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

        public bool WaitOne(TimeSpan timeout)
        {
            WaitCount++;
            return waitResult;
        }

        public void Release() => ReleaseCount++;
        public void Dispose() { }
    }
}
