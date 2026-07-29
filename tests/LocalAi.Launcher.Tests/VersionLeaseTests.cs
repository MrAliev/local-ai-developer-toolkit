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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
