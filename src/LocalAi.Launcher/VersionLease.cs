namespace LocalAi.Launcher;

public sealed class VersionLease : IDisposable
{
    private readonly FileStream _stream;

    private VersionLease(FileStream stream)
    {
        _stream = stream;
    }

    public static VersionLease AcquireShared(string lockPath)
    {
        EnsureParent(lockPath);
        return new VersionLease(new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite));
    }

    public static VersionLease AcquireExclusive(
        string lockPath,
        TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        EnsureParent(lockPath);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new VersionLease(new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None));
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
            catch (IOException)
            {
                throw new LauncherException(
                    "version_in_use",
                    "The active LocalAi version is currently in use.");
            }
        }
    }

    public void Dispose() => _stream.Dispose();

    private static void EnsureParent(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        var parent = Path.GetDirectoryName(Path.GetFullPath(lockPath))
            ?? throw new ArgumentException(
                "Version lease path has no parent directory.",
                nameof(lockPath));
        Directory.CreateDirectory(parent);
    }
}
