using LocalAi.Contracts.Activation;

namespace LocalAi.Launcher;

public sealed class VersionLease : IDisposable
{
    private readonly IDisposable _lease;

    private VersionLease(IDisposable lease)
    {
        _lease = lease;
    }

    public static VersionLease AcquireShared(string lockPath)
    {
        return new VersionLease(ActivationCoordinator.AcquireShared(BinRoot(lockPath)));
    }

    public static VersionLease AcquireExclusive(
        string lockPath,
        TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        try
        {
            return new VersionLease(
                ActivationCoordinator.AcquireExclusive(BinRoot(lockPath), timeout));
        }
        catch (ActivationCoordinationException exception)
        {
            throw new LauncherException(exception.Code, exception.Message);
        }
    }

    public void Dispose() => _lease.Dispose();

    private static string BinRoot(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        var parent = Path.GetDirectoryName(Path.GetFullPath(lockPath))
            ?? throw new ArgumentException(
                "Version lease path has no parent directory.",
                nameof(lockPath));
        Directory.CreateDirectory(parent);
        return parent;
    }
}
