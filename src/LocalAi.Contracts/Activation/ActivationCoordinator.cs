using System.Security.Cryptography;
using System.Text;

namespace LocalAi.Contracts.Activation;

public static class ActivationCoordinator
{
    private const string MutexPrefix = "LocalAi.Launcher.Activation.";

    public static ActivationExclusiveLease AcquireExclusive(
        string binRoot,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var canonicalRoot = CanonicalRoot(binRoot);
        var mutex = new Mutex(initiallyOwned: false, MutexName(canonicalRoot));
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                throw new ActivationCoordinationException(
                    "activation_timeout",
                    "Timed out waiting for another LocalAi activation.");
            }

            var lockPath = Path.Combine(canonicalRoot, "current.lock");
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                try
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.WriteThrough);
                    return new ActivationExclusiveLease(
                        canonicalRoot,
                        stream,
                        mutex,
                        ownsMutex);
                }
                catch (IOException) when (DateTimeOffset.UtcNow < deadline)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(25));
                }
                catch (IOException exception)
                {
                    throw new ActivationCoordinationException(
                        "version_in_use",
                        "The active LocalAi version is currently in use.",
                        exception);
                }
            }
        }
        catch
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }

            mutex.Dispose();
            throw;
        }
    }

    public static ActivationSharedLease AcquireShared(string binRoot)
    {
        var canonicalRoot = CanonicalRoot(binRoot);
        var stream = new FileStream(
            Path.Combine(canonicalRoot, "current.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        return new ActivationSharedLease(canonicalRoot, stream);
    }

    public static string CanonicalRoot(string binRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(binRoot));

    internal static string MutexName(string canonicalRoot)
    {
        var normalized = OperatingSystem.IsWindows()
            ? canonicalRoot.ToUpperInvariant()
            : canonicalRoot;
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return (OperatingSystem.IsWindows() ? @"Global\" : string.Empty) +
               MutexPrefix + hash;
    }
}

public sealed class ActivationExclusiveLease : IDisposable
{
    private FileStream? stream;
    private Mutex? mutex;
    private readonly bool ownsMutex;

    internal ActivationExclusiveLease(
        string binRoot,
        FileStream stream,
        Mutex mutex,
        bool ownsMutex)
    {
        BinRoot = binRoot;
        this.stream = stream;
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
    }

    public string BinRoot { get; }
    public string CurrentPath => Path.Combine(BinRoot, "current.json");
    public string LockPath => Path.Combine(BinRoot, "current.lock");

    public void Dispose()
    {
        var ownedStream = Interlocked.Exchange(ref stream, null);
        var ownedMutex = Interlocked.Exchange(ref mutex, null);
        ownedStream?.Dispose();
        if (ownedMutex is not null)
        {
            if (ownsMutex)
            {
                ownedMutex.ReleaseMutex();
            }

            ownedMutex.Dispose();
        }
    }
}

public sealed class ActivationSharedLease : IDisposable
{
    private FileStream? stream;

    internal ActivationSharedLease(string binRoot, FileStream stream)
    {
        BinRoot = binRoot;
        this.stream = stream;
    }

    public string BinRoot { get; }

    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}

public sealed class ActivationCoordinationException : Exception
{
    public ActivationCoordinationException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
